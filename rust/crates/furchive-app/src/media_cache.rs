//! Ephemeral account/query-scoped media grants and byte-bounded memory cache.
//! No renderer-supplied URL/path is ever fetched. No artwork is written to disk.
use furchive_domain::MediaItem;
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq, Hash)]
#[serde(rename_all = "snake_case")]
pub enum Variant {
    Preview,
    Full,
}
#[derive(Clone)]
struct Grant {
    generation: u64,
    id: String,
    variant: Variant,
    url: String,
}
#[derive(Clone)]
pub struct CachedMedia {
    pub bytes: Arc<Vec<u8>>,
    pub mime: String,
}
struct Entry {
    grant: Grant,
    content: CachedMedia,
}
struct Inner {
    generation: u64,
    authorized: HashMap<String, MediaItem>,
    entries: HashMap<String, Entry>,
    order: VecDeque<String>,
    bytes: usize,
    budget: usize,
}
pub struct MediaCache {
    inner: Mutex<Inner>,
    http: reqwest::Client,
    #[cfg(feature = "fixture-media")]
    fixture_origin: Option<url::Url>,
    fetch: tokio::sync::Semaphore,
}
/// Only exact upstream data/preview/sample content-addressed paths. SVG/HTML and
/// explicit default ports, query/userinfo/redirect tricks are not media authority.
pub fn canonical_media(raw: &str) -> Result<url::Url, String> {
    let url = url::Url::parse(raw).map_err(|_| "invalid media URL")?;
    if raw != url.as_str()
        || url.scheme() != "https"
        || !matches!(
            url.host_str(),
            Some("static1.e621.net" | "static2.e621.net" | "static3.e621.net")
        )
        || url.port().is_some()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err("media URL is not a canonical e621 CDN resource".into());
    }
    let parts = url.path().split('/').collect::<Vec<_>>();
    let tail = match parts.as_slice() {
        ["", "data", a, b, file] => [*a, *b, *file],
        ["", "data", "preview" | "sample", a, b, file] => [*a, *b, *file],
        _ => return Err("invalid e621 media path".into()),
    };
    let (hash, ext) = tail[2].rsplit_once('.').ok_or("missing media extension")?;
    if hash.len() != 32
        || !hash
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        || tail[0] != &hash[..2]
        || tail[1] != &hash[2..4]
        || !matches!(
            ext,
            "png" | "jpg" | "jpeg" | "gif" | "webp" | "apng" | "webm" | "mp4"
        )
    {
        return Err("invalid content-addressed media path".into());
    }
    Ok(url)
}
impl MediaCache {
    pub fn new(budget: usize) -> Result<Self, String> {
        if budget == 0 || budget > 512 * 1024 * 1024 {
            return Err("media budget must be 1 byte–512MiB".into());
        }
        Ok(Self {
            inner: Mutex::new(Inner {
                generation: 0,
                authorized: HashMap::new(),
                entries: HashMap::new(),
                order: VecDeque::new(),
                bytes: 0,
                budget,
            }),
            http: reqwest::Client::builder()
                .redirect(reqwest::redirect::Policy::none())
                .timeout(std::time::Duration::from_secs(30))
                .user_agent("Furchive/2.0-alpha (+https://github.com/saikitsune/Furchive)")
                .build()
                .map_err(|_| "cannot initialize media fetcher")?,
            #[cfg(feature = "fixture-media")]
            fixture_origin: None,
            fetch: tokio::sync::Semaphore::new(2),
        })
    }
    #[cfg(feature = "fixture-media")]
    pub fn local_fixture(budget: usize, origin: &str) -> Result<Self, String> {
        let url = url::Url::parse(origin).map_err(|_| "invalid fixture origin")?;
        if url.scheme() != "http"
            || url.host_str() != Some("127.0.0.1")
            || url.port().is_none()
            || url.path() != "/"
            || url.query().is_some()
            || url.fragment().is_some()
            || !url.username().is_empty()
            || url.password().is_some()
        {
            return Err("fixture origin must be exact IPv4 loopback".into());
        }
        let mut result = Self::new(budget)?;
        result.fixture_origin = Some(url);
        Ok(result)
    }
    /// Replace authority atomically; an old asynchronous fetch cannot republish.
    pub fn authorize(&self, posts: &[MediaItem]) -> Result<(), String> {
        let generation = self.begin_page()?;
        self.authorize_page(generation, posts)
    }
    pub fn begin_page(&self) -> Result<u64, String> {
        let mut i = self.inner.lock().map_err(|_| "media cache poisoned")?;
        i.generation = i
            .generation
            .checked_add(1)
            .ok_or("media generation exhausted")?;
        i.authorized.clear();
        i.entries.clear();
        i.order.clear();
        i.bytes = 0;
        Ok(i.generation)
    }
    pub fn authorize_page(&self, generation: u64, posts: &[MediaItem]) -> Result<(), String> {
        if posts.len() > 100 {
            return Err("media authorization page too large".into());
        }
        let mut authorized = HashMap::new();
        for post in posts {
            if post.key.source() != "e621"
                || authorized
                    .insert(post.key.id().to_owned(), post.clone())
                    .is_some()
            {
                return Err("invalid or duplicate media authorization".into());
            }
        }
        let mut i = self.inner.lock().map_err(|_| "media cache poisoned")?;
        if i.generation != generation {
            return Err("superseded result page".into());
        }
        i.authorized = authorized;
        i.entries.clear();
        i.order.clear();
        i.bytes = 0;
        Ok(())
    }
    fn grant(&self, id: &str, variant: Variant) -> Result<Grant, String> {
        let i = self.inner.lock().map_err(|_| "media cache poisoned")?;
        let post = i
            .authorized
            .get(id)
            .ok_or("post not in current authorized results")?;
        let raw = match variant {
            Variant::Preview => post.preview_url.as_ref(),
            Variant::Full => post.full_url.as_ref(),
        }
        .ok_or("media unavailable or filtered")?;
        canonical_media(raw)?;
        Ok(Grant {
            generation: i.generation,
            id: id.into(),
            variant,
            url: raw.clone(),
        })
    }
    pub fn authorized_post(&self, id: &str) -> Result<MediaItem, String> {
        self.inner
            .lock()
            .map_err(|_| "media cache poisoned")?
            .authorized
            .get(id)
            .cloned()
            .ok_or("post not in current authorized results".into())
    }
    /// Full-media only local-first path. Grants/generations are checked before I/O
    /// and again on publication. A changed completed file is an explicit error,
    /// never silent fallback that conceals local corruption.
    pub async fn prepare_with_downloads(
        &self,
        id: &str,
        variant: Variant,
        downloads: &crate::downloads::Downloads,
    ) -> Result<String, String> {
        if variant == Variant::Preview {
            return self.prepare(id, variant).await;
        }
        let grant = self.grant(id, variant)?;
        let permit = self
            .fetch
            .acquire()
            .await
            .map_err(|_| "media fetcher closed")?;
        let limit = {
            let i = self.inner.lock().map_err(|_| "media cache poisoned")?;
            if i.generation != grant.generation {
                return Err("media selection changed".into());
            }
            i.budget.min(64 * 1024 * 1024)
        };
        let key = furchive_domain::PostKey::new("e621", id).map_err(|e| e.to_string())?;
        if let Some(bytes) = downloads.completed_bytes(&key, &grant.url, limit).await? {
            let mime = validate_bytes(&grant.url, &bytes)?;
            return self.publish(grant, bytes, mime);
        }
        // Keep the original authority and semaphore admission across fallback.
        // Re-entering prepare(id, variant) would mint a newer query's grant.
        let result = self.prepare_granted(grant).await;
        drop(permit);
        result
    }
    pub async fn prepare(&self, id: &str, variant: Variant) -> Result<String, String> {
        let grant = self.grant(id, variant)?;
        let _permit = self
            .fetch
            .acquire()
            .await
            .map_err(|_| "media fetcher closed")?;
        self.prepare_granted(grant).await
    }
    /// Called only while the caller owns one fetch permit; never renew authority.
    async fn prepare_granted(&self, grant: Grant) -> Result<String, String> {
        {
            let i = self.inner.lock().map_err(|_| "media cache poisoned")?;
            if i.generation != grant.generation {
                return Err("media selection changed".into());
            }
            if let Some((handle, _)) = i
                .entries
                .iter()
                .find(|(_, e)| e.grant.id == grant.id && e.grant.variant == grant.variant)
            {
                return Ok(handle.clone());
            }
        }
        let destination = canonical_media(&grant.url)?;
        #[cfg(feature = "fixture-media")]
        let destination = if let Some(origin) = &self.fixture_origin {
            origin
                .join(destination.path())
                .map_err(|_| "invalid fixture route")?
        } else {
            destination
        };
        let mut response = self
            .http
            .get(destination)
            .send()
            .await
            .map_err(|_| "media request failed")?;
        if response.status() != reqwest::StatusCode::OK {
            return Err(format!(
                "media returned HTTP {}",
                response.status().as_u16()
            ));
        }
        let budget = self
            .inner
            .lock()
            .map_err(|_| "media cache poisoned")?
            .budget;
        let limit = budget.min(match grant.variant {
            Variant::Preview => 4 * 1024 * 1024,
            Variant::Full => 64 * 1024 * 1024,
        });
        if response.content_length().is_some_and(|n| n > limit as u64) {
            return Err("media exceeds cache/item budget".into());
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = response.chunk().await.map_err(|_| "media interrupted")? {
            if chunk.len() > limit.saturating_sub(bytes.len()) {
                return Err("media exceeds cache/item budget".into());
            }
            bytes.extend_from_slice(&chunk);
        }
        let mime = validate_bytes(&grant.url, &bytes)?;
        self.publish(grant, bytes, mime)
    }
    fn publish(&self, grant: Grant, bytes: Vec<u8>, mime: &str) -> Result<String, String> {
        let mut i = self.inner.lock().map_err(|_| "media cache poisoned")?;
        if bytes.is_empty() || bytes.len() > i.budget {
            return Err("media exceeds cache budget".into());
        }
        if i.generation != grant.generation || !i.authorized.contains_key(&grant.id) {
            return Err("media authority changed during fetch".into());
        }
        let mut entropy = [0u8; 32];
        getrandom::fill(&mut entropy).map_err(|_| "media handle randomness unavailable")?;
        let handle = entropy
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>();
        while i.bytes + bytes.len() > i.budget {
            let Some(old) = i.order.pop_front() else {
                return Err("cache accounting failure".into());
            };
            if let Some(entry) = i.entries.remove(&old) {
                i.bytes -= entry.content.bytes.len();
            }
        }
        i.bytes += bytes.len();
        i.order.push_back(handle.clone());
        i.entries.insert(
            handle.clone(),
            Entry {
                grant,
                content: CachedMedia {
                    bytes: Arc::new(bytes),
                    mime: mime.into(),
                },
            },
        );
        Ok(handle)
    }
    pub fn get(&self, handle: &str) -> Option<CachedMedia> {
        if handle.len() != 64 || !handle.bytes().all(|b| b.is_ascii_hexdigit()) {
            return None;
        }
        self.inner
            .lock()
            .ok()?
            .entries
            .get(handle)
            .map(|e| e.content.clone())
    }
    /// Change compressed resident storage only; cloned active response buffers and
    /// webview decoded surfaces have separate lifetimes and are not counted here.
    pub fn set_budget(&self, budget: usize) -> Result<(), String> {
        self.resize_after(budget, || Ok(()))
    }
    pub(crate) fn resize_after(
        &self,
        budget: usize,
        persist: impl FnOnce() -> Result<(), String>,
    ) -> Result<(), String> {
        if budget == 0 || budget > 512 * 1024 * 1024 {
            return Err("media budget must be 1 byte–512MiB".into());
        }
        let mut i = self.inner.lock().map_err(|_| "media cache poisoned")?;
        persist()?;
        while i.bytes > budget {
            let old = i.order.pop_front().ok_or("cache accounting failure")?;
            if let Some(entry) = i.entries.remove(&old) {
                i.bytes -= entry.content.bytes.len();
            }
        }
        i.budget = budget;
        Ok(())
    }
    pub fn budget_bytes(&self) -> Result<usize, String> {
        Ok(self
            .inner
            .lock()
            .map_err(|_| "media cache poisoned")?
            .budget)
    }
    pub fn resident_bytes(&self) -> Result<usize, String> {
        Ok(self.inner.lock().map_err(|_| "media cache poisoned")?.bytes)
    }
    #[cfg(feature = "cache-test-contract")]
    pub fn grant_for_test(&self, id: &str, variant: Variant) -> Result<TestGrant, String> {
        self.grant(id, variant).map(TestGrant)
    }
    #[cfg(feature = "cache-test-contract")]
    pub fn publish_for_test(
        &self,
        grant: TestGrant,
        bytes: Vec<u8>,
        mime: &str,
    ) -> Result<String, String> {
        self.publish(grant.0, bytes, mime)
    }
}
#[cfg(feature = "cache-test-contract")]
#[derive(Clone)]
pub struct TestGrant(Grant);
fn validate_bytes(url: &str, bytes: &[u8]) -> Result<&'static str, String> {
    let ext = url.rsplit('.').next().ok_or("missing extension")?;
    let mime = match ext {
        "webm" => {
            if !bytes.starts_with(&[0x1a, 0x45, 0xdf, 0xa3]) {
                return Err("invalid WebM header".into());
            }
            "video/webm"
        }
        "mp4" => {
            if bytes.get(4..8) != Some(b"ftyp") {
                return Err("invalid MP4 header".into());
            }
            "video/mp4"
        }
        _ => {
            let reader = image::ImageReader::new(std::io::Cursor::new(bytes))
                .with_guessed_format()
                .map_err(|_| "unrecognized image")?;
            let format = reader.format().ok_or("unrecognized image")?;
            let (w, h) = reader
                .into_dimensions()
                .map_err(|_| "invalid image dimensions")?;
            if w == 0
                || h == 0
                || w > 16384
                || h > 16384
                || u64::from(w) * u64::from(h) > 32_000_000
            {
                return Err("image exceeds decoded dimension budget".into());
            }
            match (ext, format) {
                ("png" | "apng", image::ImageFormat::Png) => "image/png",
                ("jpg" | "jpeg", image::ImageFormat::Jpeg) => "image/jpeg",
                ("gif", image::ImageFormat::Gif) => "image/gif",
                ("webp", image::ImageFormat::WebP) => "image/webp",
                _ => return Err("image extension/content mismatch".into()),
            }
        }
    };
    Ok(mime)
}

#[cfg(test)]
mod tests {
    #[test]
    fn real_owned_corpus_headers_refuse_corrupt_oversized_and_mislabeled_images() {
        let url = "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.";
        for (ext, bytes, mime) in [
            (
                "png",
                include_bytes!("../../../apps/desktop/public/fixtures/still.png").as_slice(),
                "image/png",
            ),
            (
                "gif",
                include_bytes!("../../../apps/desktop/public/fixtures/viewer-animation.gif")
                    .as_slice(),
                "image/gif",
            ),
            (
                "apng",
                include_bytes!("../../../apps/desktop/public/fixtures/viewer-animation.apng")
                    .as_slice(),
                "image/png",
            ),
            (
                "webp",
                include_bytes!("../../../apps/desktop/public/fixtures/viewer-animation.webp")
                    .as_slice(),
                "image/webp",
            ),
            (
                "webm",
                include_bytes!("../../../apps/desktop/public/fixtures/vp8.webm").as_slice(),
                "video/webm",
            ),
            (
                "mp4",
                include_bytes!("../../../apps/desktop/public/fixtures/h264.mp4").as_slice(),
                "video/mp4",
            ),
        ] {
            assert_eq!(
                super::validate_bytes(&format!("{url}{ext}"), bytes).unwrap(),
                mime
            );
        }
        for bytes in [
            include_bytes!("../../../apps/desktop/public/fixtures/viewer-corrupt.png").as_slice(),
            include_bytes!("../../../apps/desktop/public/fixtures/viewer-oversized.png").as_slice(),
            include_bytes!("../../../apps/desktop/public/fixtures/viewer-truncated.png").as_slice(),
            b"<svg onload='evil'>",
        ] {
            assert!(super::validate_bytes(&format!("{url}png"), bytes).is_err());
        }
        assert!(
            super::validate_bytes(
                &format!("{url}gif"),
                include_bytes!("../../../apps/desktop/public/fixtures/still.png")
            )
            .is_err()
        );
    }
}
