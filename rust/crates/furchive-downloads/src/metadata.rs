//! Versioned normalized snapshots, never raw upstream responses or renderer data.
use crate::{Engine, Error, Intent, Job, Result, State};
use furchive_domain::{MediaItem, MediaKind, PostKey, Rating, TagCategory};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::io::Write;

pub const METADATA_LIMIT: usize = 1024 * 1024;
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum MetadataStatus {
    #[default]
    NotRequested,
    Pending,
    Written,
    Failed,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(try_from = "SnapshotFields")]
pub struct NormalizedMetadata(SnapshotFields);
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct SnapshotFields {
    representation: String,
    #[serde(deserialize_with = "strict_key")]
    key: PostKey,
    url: String,
    title: String,
    description: String,
    tags: BTreeMap<TagCategory, Vec<String>>,
    artists: Vec<String>,
    kind: MediaKind,
    rating: Rating,
    created_at: Option<String>,
    fetched_at: Option<i64>,
    score: i64,
    favorite_count: u64,
    file_extension: Option<String>,
    file_size_bytes: Option<u64>,
    file_md5: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
}
fn strict_key<'de, D: serde::Deserializer<'de>>(de: D) -> std::result::Result<PostKey, D::Error> {
    #[derive(Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Parts {
        source: String,
        id: String,
    }
    let p = Parts::deserialize(de)?;
    PostKey::new(p.source, p.id).map_err(serde::de::Error::custom)
}
impl TryFrom<SnapshotFields> for NormalizedMetadata {
    type Error = Error;
    fn try_from(fields: SnapshotFields) -> Result<Self> {
        let snapshot = Self(fields);
        snapshot.bytes()?;
        Ok(snapshot)
    }
}
impl NormalizedMetadata {
    fn from_media(media: &MediaItem) -> Result<Self> {
        Self::try_from(SnapshotFields {
            representation: "normalized-furchive-v1".into(),
            key: media.key.clone(),
            url: media
                .full_url
                .clone()
                .ok_or_else(|| Error::Invalid("metadata missing media URL".into()))?,
            title: media.title.clone(),
            description: media.description.clone(),
            tags: media.tags.clone(),
            artists: media.artists.clone(),
            kind: media.kind.clone(),
            rating: media.rating.clone(),
            created_at: media.created_at.clone(),
            fetched_at: media.fetched_at,
            score: media.score,
            favorite_count: media.favorite_count,
            file_extension: media.file_extension.clone(),
            file_size_bytes: media.file_size_bytes,
            file_md5: media.file_md5.clone(),
            width: media.width,
            height: media.height,
        })
    }
    pub(crate) fn bytes(&self) -> Result<Vec<u8>> {
        let f = &self.0;
        let url = crate::model::validate_url(&f.url)?;
        if f.representation != "normalized-furchive-v1"
            || f.key.source() != "e621"
            || f.key.id().len() > 20
            || !f.key.id().bytes().all(|b| b.is_ascii_digit())
            || f.key.id().parse::<u64>().ok().is_none_or(|n| n == 0)
            || url.query().is_some()
            || f.file_size_bytes.is_some_and(|n| n > i64::MAX as u64)
            || f.file_md5
                .as_ref()
                .is_some_and(|s| s.len() != 32 || !s.bytes().all(|b| b.is_ascii_hexdigit()))
            || f.file_extension.as_ref().is_some_and(|s| {
                s.is_empty() || s.len() > 16 || !s.bytes().all(|b| b.is_ascii_alphanumeric())
            })
        {
            return Err(Error::Invalid("invalid normalized metadata".into()));
        }
        let bytes = serde_json::to_vec(self)?;
        if bytes.len() > METADATA_LIMIT {
            return Err(Error::Invalid("metadata exceeds 1MiB".into()));
        }
        Ok(bytes)
    }
    pub(crate) fn validate_for(&self, intent: &Intent) -> Result<()> {
        self.bytes()?;
        if self.0.key != intent.key
            || self.0.url != intent.url
            || self.0.file_size_bytes != intent.size
        {
            return Err(Error::Invalid(
                "metadata does not match admitted post/URL/size".into(),
            ));
        }
        Ok(())
    }
}
impl Intent {
    /// Trusted host-only admission API. The renderer never supplies this snapshot.
    pub fn with_metadata(mut self, media: &MediaItem) -> Result<Self> {
        self.metadata = Some(NormalizedMetadata::from_media(media)?);
        self.validate()?;
        Ok(self)
    }
}
impl Engine {
    pub(crate) fn finish_metadata(&self, job: &mut Job) -> Result<()> {
        if job.state != State::Completed
            || !matches!(
                job.metadata_status,
                MetadataStatus::Pending | MetadataStatus::Written
            )
        {
            return Ok(());
        }
        let result = if job.metadata_status == MetadataStatus::Written {
            self.verify_written_metadata(job)
        } else {
            self.write_metadata(job)
        };
        if let Err(error) = result {
            job.metadata_status = MetadataStatus::Failed;
            // Do not serialize OS messages containing private host paths.
            job.metadata_error = Some(match error {
                Error::Invalid(s) => s,
                _ => "metadata publication failed; existing files preserved".into(),
            });
        }
        self.save(job).or_else(|_| {
            job.metadata_status = MetadataStatus::Failed;
            job.metadata_error = Some("metadata acknowledgement could not be persisted".into());
            self.save(job)
        })
    }
    fn verify_written_metadata(&self, job: &Job) -> Result<()> {
        use sha2::Digest;
        use std::io::Read;
        let bytes = job
            .intent
            .metadata
            .as_ref()
            .ok_or_else(|| Error::Invalid("missing metadata".into()))?
            .bytes()?;
        let relative = format!("{}.metadata.json", job.relative_path);
        self.check_destination(&relative)?;
        let file = self.grant.destination(&relative)?;
        let (identity, digest) = job
            .metadata_publication
            .clone()
            .ok_or_else(|| Error::Invalid("missing metadata receipt".into()))?
            .parts();
        if crate::files::identity(&file)? != identity
            || digest != format!("{:x}", sha2::Sha256::digest(&bytes))
            || file.metadata()?.len() != bytes.len() as u64
        {
            return Err(Error::Invalid(
                "written metadata identity/size changed".into(),
            ));
        }
        let mut existing = Vec::new();
        file.take(METADATA_LIMIT as u64 + 1)
            .read_to_end(&mut existing)?;
        if existing != bytes {
            return Err(Error::Invalid("written metadata bytes changed".into()));
        }
        Ok(())
    }
    fn write_metadata(&self, job: &mut Job) -> Result<()> {
        #[cfg(test)]
        if self.crash_at.load(std::sync::atomic::Ordering::SeqCst) == 9 {
            // This test-only held synchronous writer must surrender its Tokio
            // scheduler core, otherwise a queued manager wake can be starved.
            tokio::task::block_in_place(|| -> Result<()> {
                let dir = &self.grant.root;
                let marker = self
                    .grant
                    .metadata_stage(dir, "metadata-writer-held", None, b"")?;
                marker.sync_all()?;
                let deadline = std::time::Instant::now() + std::time::Duration::from_secs(8);
                while !self.grant.metadata_exists(dir, "metadata-writer-release")? {
                    assert!(
                        std::time::Instant::now() < deadline,
                        "test did not release metadata writer"
                    );
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
                Ok(())
            })?;
        }
        let snapshot = job
            .intent
            .metadata
            .as_ref()
            .ok_or_else(|| Error::Invalid("missing admitted metadata".into()))?;
        let bytes = snapshot.bytes()?;
        let mut media = self.grant.destination(&job.relative_path)?;
        let (identity, digest) = job
            .publication
            .clone()
            .ok_or_else(|| Error::Invalid("media lacks publication receipt".into()))?
            .parts();
        if crate::files::identity(&media)? != identity
            || crate::worker::validate_file(&mut media, job)? != digest
        {
            return Err(Error::Invalid(
                "completed media changed; metadata refused".into(),
            ));
        }
        let relative = format!("{}.metadata.json", job.relative_path);
        crate::files::relative(&relative)?;
        self.check_destination(&relative)?;
        let (dir, name) = self.grant.read_parent(&relative)?;
        // Revalidate the opened media against the same parent namespace used below.
        let current = self
            .grant
            .metadata_read(&dir, job.relative_path.rsplit('/').next().unwrap())?;
        if crate::files::identity(&current)? != identity {
            return Err(Error::Invalid("media namespace changed".into()));
        }
        let temp = format!(".furchive-metadata-{}.tmp", job.id);
        if self.grant.metadata_exists(&dir, &name)? {
            let identity = self.grant.match_metadata(&dir, &name, &bytes)?;
            use sha2::Digest;
            job.metadata_publication = Some(crate::model::Publication::Native {
                identity,
                digest: format!("{:x}", sha2::Sha256::digest(&bytes)),
            });
        } else {
            // A receipt can name the former final sidecar, not a surviving stage.
            // Only reuse it when that stage actually exists. If absent, exclusive
            // creation still refuses a racing/unclaimed file, without overwriting it.
            let fresh = !self.grant.metadata_exists(&dir, &temp)?;
            let receipt =
                if fresh {
                    None
                } else {
                    Some(job.metadata_publication.as_ref().ok_or_else(|| {
                        Error::Invalid("unclaimed metadata stage; preserved".into())
                    })?)
                };
            #[cfg(test)]
            self.metadata_kill_seam(10); // before new stage creation
            let mut stage = self.grant.metadata_stage(&dir, &temp, receipt, &bytes)?;
            #[cfg(test)]
            self.metadata_kill_seam(11); // created/synced, not yet claimed durably
            job.metadata_publication = Some(crate::model::Publication::Native {
                identity: crate::files::identity(&stage)?,
                digest: {
                    use sha2::Digest;
                    format!("{:x}", sha2::Sha256::digest(&bytes))
                },
            });
            self.save(job)?;
            #[cfg(test)]
            self.metadata_kill_seam(12); // claimed, still empty before bounded write
            if fresh {
                stage.write_all(&bytes)?;
                stage.sync_all()?;
            }
            #[cfg(test)]
            self.metadata_kill_seam(7);
            #[cfg(test)]
            if self.crash_at.load(std::sync::atomic::Ordering::SeqCst) == 5 {
                return Err(Error::Invalid(
                    "injected metadata crash before publication".into(),
                ));
            }
            self.grant.publish_metadata(&dir, &temp, &stage, &name)?;
            #[cfg(test)]
            self.metadata_kill_seam(8);
            #[cfg(test)]
            if self.crash_at.load(std::sync::atomic::Ordering::SeqCst) == 6 {
                return Err(Error::Invalid(
                    "injected metadata crash after publication".into(),
                ));
            }
        }
        crate::files::sync_directory(&dir)?;
        job.metadata_status = MetadataStatus::Written;
        job.metadata_error = None;
        Ok(())
    }
    pub(crate) fn retry_metadata(&self, job: &mut Job) -> Result<()> {
        if job.state != State::Completed
            || job.intent.metadata.is_none()
            || job.metadata_status != MetadataStatus::Failed
            || job.metadata_attempts >= 3
        {
            return Err(Error::Invalid("metadata retry unavailable".into()));
        }
        job.metadata_attempts += 1;
        job.metadata_status = MetadataStatus::Pending;
        job.metadata_error = None;
        self.save(job)?;
        self.finish_metadata(job)
    }
    #[cfg(test)]
    fn metadata_kill_seam(&self, phase: u8) {
        if self.crash_at.load(std::sync::atomic::Ordering::SeqCst) == phase {
            let root = std::env::var("FURCHIVE_METADATA_CHILD_ROOT").unwrap();
            std::fs::write(std::path::Path::new(&root).join("kill-ready"), b"ready").unwrap();
            std::thread::sleep(std::time::Duration::from_secs(30));
            panic!("parent failed to kill child at metadata boundary");
        }
    }
}
