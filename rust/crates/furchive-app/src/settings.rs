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
    pub last_session: Option<crate::browsing_session::BrowsingSession>,
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
            last_session: None,
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
/// Sample paths only: parsing/rendering grants no filesystem or network authority.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct TemplatePreview {
    pub filename: String,
    pub pool_filename: String,
}
pub fn preview_templates(
    filename_template: &str,
    pool_filename_template: &str,
) -> Result<TemplatePreview, String> {
    let values = furchive_domain::TemplateValues {
        source: "e621",
        artist: "sample_artist",
        id: "12345",
        safe_title: "Sample artwork",
        pool_name: "Sample pool",
        page_number: "7",
        ext: "png",
    };
    let render = |template: &str| {
        FilenameTemplate::parse(template)
            .and_then(|t| t.render(&values))
            .map_err(|e| e.to_string())
    };
    Ok(TemplatePreview {
        filename: render(filename_template)?,
        pool_filename: render(pool_filename_template)?,
    })
}

pub struct SettingsStore {
    path: PathBuf,
    value: std::sync::Mutex<SettingsState>,
}
struct SettingsState {
    settings: Settings,
    revision: u64,
    request: u64,
}
impl SettingsStore {
    pub fn open(path: &Path) -> Result<Self, String> {
        Ok(Self {
            path: path.to_owned(),
            value: std::sync::Mutex::new(SettingsState {
                settings: Settings::load(path)?,
                revision: 0,
                request: 0,
            }),
        })
    }
    pub fn current(&self) -> Result<Settings, String> {
        self.value
            .lock()
            .map(|s| s.settings.clone())
            .map_err(|_| "settings lock poisoned".into())
    }
    pub fn browsing_session(
        &self,
    ) -> Result<Option<crate::browsing_session::BrowsingSession>, String> {
        let state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        Ok(if state.settings.restore_session {
            state.settings.last_session.clone()
        } else {
            None
        })
    }
    pub fn session_ticket(
        &self,
        request: u64,
    ) -> Result<Option<crate::browsing_session::SessionTicket>, String> {
        let mut state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        if request < state.request {
            return Err("browsing request superseded".into());
        }
        state.request = request;
        Ok(state
            .settings
            .restore_session
            .then_some(crate::browsing_session::SessionTicket {
                revision: state.revision,
                request,
            }))
    }
    pub fn remember_session(
        &self,
        ticket: crate::browsing_session::SessionTicket,
        session: crate::browsing_session::BrowsingSession,
    ) -> Result<(), String> {
        session.validate()?;
        let mut state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        if !state.settings.restore_session
            || state.revision != ticket.revision
            || state.request != ticket.request
        {
            return Err("remembered session superseded".into());
        }
        let mut next = state.settings.clone();
        next.last_session = Some(session);
        next.save(&self.path)?;
        state.settings = next;
        Ok(())
    }
    /// Revoke in-flight capture across account/cache boundaries, without deleting saved intent.
    pub fn invalidate_session_capture(&self) -> Result<(), String> {
        let mut state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        state.revision = state
            .revision
            .checked_add(1)
            .ok_or("session revision exhausted")?;
        Ok(())
    }
    pub fn forget_browsing_session(&self) -> Result<(), String> {
        let mut state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        let revision = state
            .revision
            .checked_add(1)
            .ok_or("session revision exhausted")?;
        let mut next = state.settings.clone();
        next.last_session = None;
        next.save(&self.path)?;
        state.settings = next;
        state.revision = revision;
        Ok(())
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
        let mut state = self.value.lock().map_err(|_| "settings lock poisoned")?;
        let revision = state
            .revision
            .checked_add(1)
            .ok_or("session revision exhausted")?;
        let current = &mut state.settings;
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
            last_session: if p.restore_session {
                current.last_session.clone()
            } else {
                None
            },
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
        state.revision = revision;
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
        if let Some(session) = &self.last_session {
            session.validate()?;
        }
        if !self.restore_session && self.last_session.is_some() {
            return Err("remembered session requires opt-in".into());
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
