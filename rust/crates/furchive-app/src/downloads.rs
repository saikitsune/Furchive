//! Product queue ownership: explicit host grant, no legacy or settings-path grants.
use furchive_domain::{FilenameTemplate, MediaItem, TemplateValues};
use furchive_downloads::{Control, DuplicatePolicy, Engine, Intent, Worker};
use serde::Serialize;
use std::{
    path::{Path, PathBuf},
    sync::Arc,
};
#[derive(Serialize)]
pub struct JobView {
    pub id: i64,
    pub post_id: String,
    pub state: String,
    pub bytes: u64,
    pub total_bytes: Option<u64>,
    pub bytes_per_second: Option<f64>,
    pub eta_seconds: Option<f64>,
    pub relative_path: String,
    pub error: Option<String>,
    pub group_id: Option<String>,
    pub group_label: Option<String>,
    pub group_position: Option<u32>,
}
struct Active {
    engine: Arc<Engine>,
    worker: Worker,
    root: PathBuf,
    concurrency: usize,
}
pub struct Downloads {
    db: PathBuf,
    active: tokio::sync::Mutex<Option<Active>>,
    progress: std::sync::Mutex<crate::download_progress::ProgressSampler>,
    #[cfg(feature = "fixture-downloads")]
    origin: Option<String>,
}
impl Downloads {
    pub fn new(db: PathBuf) -> Self {
        Self {
            db,
            active: tokio::sync::Mutex::new(None),
            progress: std::sync::Mutex::new(Default::default()),
            #[cfg(feature = "fixture-downloads")]
            origin: None,
        }
    }
    #[cfg(feature = "fixture-downloads")]
    pub fn fixture(db: PathBuf, origin: String) -> Result<Self, String> {
        let mut result = Self::new(db);
        result.origin = Some(origin);
        Ok(result)
    }
    pub async fn select_root(&self, root: &Path) -> Result<(), String> {
        self.select_root_with_concurrency(root, 1).await
    }
    pub async fn select_root_with_concurrency(
        &self,
        root: &Path,
        concurrency: usize,
    ) -> Result<(), String> {
        if !(1..=4).contains(&concurrency) {
            return Err("download concurrency must be 1–4".into());
        }
        let mut active = self.active.lock().await;
        if let Some(value) = active.as_ref() {
            return if value.root == root && value.concurrency == concurrency {
                Ok(())
            } else {
                Err("download root already granted; restart before selecting another root".into())
            };
        }
        let engine =
            Engine::open(&self.db, root).map_err(|_| "download directory or queue unavailable")?;
        #[cfg(feature = "fixture-downloads")]
        let engine = if let Some(origin) = &self.origin {
            drop(engine);
            Engine::fixture_http(&self.db, root, origin)
                .map_err(|_| "invalid test download directory or origin")?
        } else {
            engine
        };
        engine
            .hold_restored()
            .map_err(|_| "cannot hold restored downloads")?;
        let engine = Arc::new(engine);
        let worker = engine
            .start_with_concurrency(concurrency)
            .map_err(|_| "download worker unavailable")?;
        *active = Some(Active {
            engine,
            worker,
            root: root.to_owned(),
            concurrency,
        });
        Ok(())
    }
    pub async fn enqueue(
        &self,
        post: &MediaItem,
        settings: &crate::settings::Settings,
    ) -> Result<i64, String> {
        let active = self.active.lock().await;
        let value = active.as_ref().ok_or("select a download directory first")?;
        settings.validate()?;
        crate::media_cache::canonical_media(post.full_url.as_deref().ok_or("media unavailable")?)?;
        let template =
            FilenameTemplate::parse(&settings.filename_template).map_err(|e| e.to_string())?;
        let artist = post.artists.join(",");
        let id = post.key.id();
        let ext = post
            .file_extension
            .as_deref()
            .ok_or("missing file extension")?;
        let values = TemplateValues {
            source: "e621",
            artist: &artist,
            id,
            safe_title: &post.title,
            ext,
            ..Default::default()
        };
        let policy = match settings.duplicates {
            crate::settings::DuplicatePolicy::Skip => DuplicatePolicy::Skip,
            crate::settings::DuplicatePolicy::Overwrite => DuplicatePolicy::Overwrite,
            crate::settings::DuplicatePolicy::Rename => DuplicatePolicy::Rename,
        };
        let intent = Intent::new(
            post,
            &template,
            &values,
            policy,
            post.file_md5
                .clone()
                .map(furchive_downloads::ExpectedHash::Md5),
        )
        .map_err(|e| e.to_string())?;
        // Persist paused first: explicit Resume is the network-start intent. No restored
        // queued job can run before the host directory has again been granted.
        value
            .engine
            .enqueue_paused(intent)
            .map_err(|e| e.to_string())
    }
    /// Current bounded page only: hidden/unavailable members retain ordinal holes.
    pub async fn enqueue_pool_page(
        &self,
        page: &crate::browse::PoolPage,
        settings: &crate::settings::Settings,
    ) -> Result<String, String> {
        if page.entries.len() > 100 {
            return Err("pool page exceeds 100 entries".into());
        }
        self.enqueue_pool_snapshot(page, settings, false).await
    }
    pub async fn enqueue_whole_pool(
        &self,
        page: &crate::browse::PoolPage,
        settings: &crate::settings::Settings,
    ) -> Result<String, String> {
        if page.pool.post_ids.len() > 10_000
            || page.total != page.pool.post_ids.len()
            || page.entries.len() != page.total
            || page.next_offset.is_some()
            || page
                .entries
                .iter()
                .enumerate()
                .any(|(n, e)| e.position != n || page.pool.post_ids.get(n) != Some(&e.id))
        {
            return Err("whole-pool snapshot incomplete or invalid".into());
        }
        self.enqueue_pool_snapshot(page, settings, true).await
    }
    async fn enqueue_pool_snapshot(
        &self,
        page: &crate::browse::PoolPage,
        settings: &crate::settings::Settings,
        whole: bool,
    ) -> Result<String, String> {
        settings.validate()?;
        crate::browse::pool_slice(&page.pool, 0, 1)?;
        let template =
            FilenameTemplate::parse(&settings.pool_filename_template).map_err(|e| e.to_string())?;
        let mut intents = Vec::new();
        for entry in &page.entries {
            if let Some(post) = &entry.post {
                if page.pool.post_ids.get(entry.position) != Some(&entry.id)
                    || post.key.id() != entry.id
                {
                    return Err("pool download membership mismatch".into());
                }
                crate::media_cache::canonical_media(
                    post.full_url
                        .as_deref()
                        .ok_or("pool member file unavailable")?,
                )?;
                let artist = post.artists.join(",");
                let number = (entry.position + 1).to_string();
                let values = TemplateValues {
                    source: "e621",
                    artist: &artist,
                    id: post.key.id(),
                    safe_title: &post.title,
                    pool_name: &page.pool.name,
                    page_number: &number,
                    ext: post
                        .file_extension
                        .as_deref()
                        .ok_or("pool member extension unavailable")?,
                };
                let policy = match settings.duplicates {
                    crate::settings::DuplicatePolicy::Skip => DuplicatePolicy::Skip,
                    crate::settings::DuplicatePolicy::Overwrite => DuplicatePolicy::Overwrite,
                    crate::settings::DuplicatePolicy::Rename => DuplicatePolicy::Rename,
                };
                let intent = Intent::new(
                    post,
                    &template,
                    &values,
                    policy,
                    post.file_md5
                        .clone()
                        .map(furchive_downloads::ExpectedHash::Md5),
                )
                .map_err(|e| e.to_string())?;
                intents.push((
                    u32::try_from(entry.position).map_err(|_| "pool position too large")?,
                    intent,
                ));
            }
        }
        let mut random = [0u8; 16];
        getrandom::fill(&mut random).map_err(|_| "group identity randomness unavailable")?;
        let id = format!(
            "pool-{}-{}",
            page.pool.key.id(),
            random
                .iter()
                .map(|b| format!("{b:02x}"))
                .collect::<String>()
        );
        let active = self.active.lock().await;
        let value = active.as_ref().ok_or("select a download directory first")?;
        if whole {
            value
                .engine
                .enqueue_whole_group_paused(&id, &page.pool.name, intents)
        } else {
            value
                .engine
                .enqueue_group_paused(&id, &page.pool.name, intents)
        }
        .map_err(|e| e.to_string())?;
        Ok(id)
    }
    pub async fn control_group(&self, id: &str, verb: &str) -> Result<(), String> {
        let active = self.active.lock().await;
        let value = active.as_ref().ok_or("select a download directory first")?;
        let control = match verb {
            "resume" => Control::Resume,
            "pause" => Control::Pause,
            "cancel" => Control::Cancel,
            _ => return Err("unknown download control".into()),
        };
        let jobs = value
            .engine
            .jobs()
            .map_err(|e| e.to_string())?
            .into_iter()
            .filter(|j| j.group_id.as_deref() == Some(id))
            .collect::<Vec<_>>();
        if jobs.is_empty() {
            return Err("unknown download group".into());
        }
        for job in jobs {
            self.progress
                .lock()
                .map_err(|_| "download progress poisoned")?
                .forget(job.id);
            value
                .worker
                .control(job.id, control)
                .await
                .map_err(|e| e.to_string())?;
        }
        Ok(())
    }
    /// Only a current explicit grant supplies filesystem authority. Offload bounded
    /// descriptor-relative read/hash from the async/UI executor.
    pub async fn completed_bytes(
        &self,
        key: &furchive_domain::PostKey,
        expected_url: &str,
        limit: usize,
    ) -> Result<Option<Vec<u8>>, String> {
        let engine = {
            let active = self.active.lock().await;
            active.as_ref().map(|a| a.engine.clone())
        };
        let Some(engine) = engine else {
            return Ok(None);
        };
        let key = key.clone();
        let expected_url = expected_url.to_owned();
        tokio::task::spawn_blocking(move || {
            engine
                .completed_bytes_for(&key, &expected_url, limit)
                .map_err(|e| e.to_string())
        })
        .await
        .map_err(|_| "local media reader stopped".to_string())?
    }
    pub async fn jobs(&self) -> Result<Vec<JobView>, String> {
        let active = self.active.lock().await;
        let Some(value) = active.as_ref() else {
            return Ok(Vec::new());
        };
        let jobs = value.engine.jobs().map_err(|e| e.to_string())?;
        let mut sampler = self
            .progress
            .lock()
            .map_err(|_| "download progress poisoned")?;
        sampler.retain(&jobs.iter().map(|j| j.id).collect::<Vec<_>>());
        Ok(jobs
            .into_iter()
            .map(|j| {
                let total = j.expected_total();
                let rate = sampler.sample(
                    j.id,
                    crate::download_progress::Observation {
                        bytes: j.bytes,
                        total,
                        running: j.state == furchive_downloads::State::Running,
                    },
                    std::time::Instant::now(),
                );
                JobView {
                    id: j.id,
                    post_id: j.key.id().into(),
                    state: format!("{:?}", j.state),
                    bytes: j.bytes,
                    total_bytes: total,
                    bytes_per_second: rate.bytes_per_second,
                    eta_seconds: rate.eta_seconds,
                    relative_path: j.relative_path,
                    error: j.error,
                    group_id: j.group_id,
                    group_label: j.group_label,
                    group_position: j.group_position,
                }
            })
            .collect())
    }
    pub async fn control(&self, id: i64, verb: &str) -> Result<(), String> {
        let control = match verb {
            "pause" => Control::Pause,
            "resume" => Control::Resume,
            "cancel" => Control::Cancel,
            _ => return Err("unknown download control".into()),
        };
        let active = self.active.lock().await;
        let value = active.as_ref().ok_or("select a download directory first")?;
        self.progress
            .lock()
            .map_err(|_| "download progress poisoned")?
            .forget(id);
        value
            .worker
            .control(id, control)
            .await
            .map_err(|e| e.to_string())?;
        Ok(())
    }
    pub async fn shutdown(&self) -> Result<(), String> {
        let mut active = self.active.lock().await;
        self.progress
            .lock()
            .map_err(|_| "download progress poisoned")?
            .clear();
        if let Some(value) = active.take() {
            value.worker.shutdown().await.map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}
