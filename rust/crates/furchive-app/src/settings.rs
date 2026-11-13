//! Non-secret settings with validation and same-directory atomic replacement.
use furchive_domain::FilenameTemplate;
use serde::{Deserialize, Serialize};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DuplicatePolicy {
    Skip,
    Overwrite,
    Rename,
}
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(default, deny_unknown_fields)]
pub struct Settings {
    pub version: u32,
    pub download_directory: Option<PathBuf>,
    pub filename_template: String,
    pub pool_filename_template: String,
    pub concurrent_downloads: u8,
    pub duplicates: DuplicatePolicy,
    pub gallery_scale: f64,
    pub cache_limit_bytes: u64,
    pub restore_session: bool,
    pub save_metadata: bool,
    pub start_muted: bool,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            version: 1,
            download_directory: None,
            filename_template: "{source}/{artist}/{id}.{ext}".into(),
            pool_filename_template: "{source}/pools/{artist}/{pool_name}/{page_number}_{id}.{ext}"
                .into(),
            concurrent_downloads: 3,
            duplicates: DuplicatePolicy::Skip,
            gallery_scale: 1.0,
            cache_limit_bytes: 512 * 1024 * 1024,
            restore_session: false,
            save_metadata: false,
            start_muted: true,
        }
    }
}
/// Browser-editable non-secret preferences deliberately exclude directory grants.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Preferences {
    pub filename_template: String,
    pub pool_filename_template: String,
    pub concurrent_downloads: u8,
    pub duplicates: DuplicatePolicy,
    pub gallery_scale: f64,
    pub cache_limit_bytes: u64,
    pub restore_session: bool,
    pub save_metadata: bool,
    pub start_muted: bool,
}
impl From<Settings> for Preferences {
    fn from(s: Settings) -> Self {
        Self {
            filename_template: s.filename_template,
            pool_filename_template: s.pool_filename_template,
            concurrent_downloads: s.concurrent_downloads,
            duplicates: s.duplicates,
            gallery_scale: s.gallery_scale,
            cache_limit_bytes: s.cache_limit_bytes,
            restore_session: s.restore_session,
            save_metadata: s.save_metadata,
            start_muted: s.start_muted,
        }
    }
}
pub struct SettingsStore {
    path: PathBuf,
    value: std::sync::Mutex<Settings>,
}
impl SettingsStore {
    pub fn open(path: &Path) -> Result<Self, String> {
        Ok(Self {
            path: path.to_owned(),
            value: std::sync::Mutex::new(Settings::load(path)?),
        })
    }
    pub fn current(&self) -> Result<Settings, String> {
        self.value
            .lock()
            .map(|s| s.clone())
            .map_err(|_| "settings lock poisoned".into())
    }
    pub fn apply(&self, p: Preferences) -> Result<Settings, String> {
        self.apply_inner(p, None)
    }
    pub fn apply_with_cache(
        &self,
        p: Preferences,
        cache: &crate::media_cache::MediaCache,
    ) -> Result<Settings, String> {
        self.apply_inner(p, Some(cache))
    }
    fn apply_inner(
        &self,
        p: Preferences,
        cache: Option<&crate::media_cache::MediaCache>,
    ) -> Result<Settings, String> {
        let mut current = self.value.lock().map_err(|_| "settings lock poisoned")?;
        let next = Settings {
            version: current.version,
            download_directory: current.download_directory.clone(),
            filename_template: p.filename_template,
            pool_filename_template: p.pool_filename_template,
            concurrent_downloads: p.concurrent_downloads,
            duplicates: p.duplicates,
            gallery_scale: p.gallery_scale,
            cache_limit_bytes: p.cache_limit_bytes,
            restore_session: p.restore_session,
            save_metadata: p.save_metadata,
            start_muted: p.start_muted,
        };
        next.validate()?;
        if let Some(cache) = cache {
            cache.resize_after(next.resident_media_budget(), || next.save(&self.path))?;
        } else {
            next.save(&self.path)?;
        }
        *current = next.clone();
        Ok(next)
    }
}

impl Settings {
    /// Resident compressed media is conservatively capped even for legacy large cache settings.
    pub fn resident_media_budget(&self) -> usize {
        self.cache_limit_bytes.min(512 * 1024 * 1024) as usize
    }
    pub fn validate(&self) -> Result<(), String> {
        if self.version != 1 {
            return Err("unsupported settings version".into());
        }
        if !(1..=4).contains(&self.concurrent_downloads) {
            return Err("download concurrency must be 1–4".into());
        }
        if !self.gallery_scale.is_finite() || !(0.5..=2.0).contains(&self.gallery_scale) {
            return Err("gallery scale must be 0.5–2.0".into());
        }
        if !(16 * 1024 * 1024..=16 * 1024 * 1024 * 1024).contains(&self.cache_limit_bytes) {
            return Err("cache budget must be 16MiB–16GiB".into());
        }
        FilenameTemplate::parse(&self.filename_template).map_err(|e| e.to_string())?;
        FilenameTemplate::parse(&self.pool_filename_template).map_err(|e| e.to_string())?;
        if self
            .download_directory
            .as_ref()
            .is_some_and(|p| !p.is_absolute())
        {
            return Err("download directory must be absolute".into());
        }
        Ok(())
    }
    pub fn load(path: &Path) -> Result<Self, String> {
        let file = match std::fs::File::open(path) {
            Ok(f) => f,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Self::default()),
            Err(_) => return Err("cannot open settings".into()),
        };
        let mut bytes = Vec::new();
        file.take(1024 * 1024 + 1)
            .read_to_end(&mut bytes)
            .map_err(|_| "cannot read settings")?;
        if bytes.len() > 1024 * 1024 {
            return Err("settings exceeds 1MiB".into());
        }
        let settings: Self = serde_json::from_slice(&bytes).map_err(|_| "invalid settings JSON")?;
        settings.validate()?;
        Ok(settings)
    }
    pub fn save(&self, path: &Path) -> Result<(), String> {
        self.validate()?;
        let parent = path.parent().ok_or("settings has no parent directory")?;
        let mut temp =
            tempfile::NamedTempFile::new_in(parent).map_err(|_| "cannot stage settings")?;
        let bytes = serde_json::to_vec_pretty(self).map_err(|_| "cannot serialize settings")?;
        if bytes.len() > 1024 * 1024 {
            return Err("settings exceeds 1MiB".into());
        }
        temp.write_all(&bytes)
            .map_err(|_| "cannot write settings")?;
        temp.as_file()
            .sync_all()
            .map_err(|_| "cannot flush settings")?;
        temp.persist(path)
            .map_err(|_| "cannot atomically replace settings")?;
        #[cfg(unix)]
        std::fs::File::open(parent)
            .and_then(|f| f.sync_all())
            .map_err(|_| "settings replaced but directory durability was not confirmed")?;
        Ok(())
    }
}
