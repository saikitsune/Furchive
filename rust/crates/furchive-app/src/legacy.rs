//! Explicit, offline legacy user-data recovery. Nothing is imported on open.
//!
//! See `docs/legacy-import.md` for the trust boundary and collision policy.
mod json;
mod sqlite;
#[cfg(test)]
mod tests;
mod transfer;
mod wal;

pub use transfer::{ConfirmedImport, ImportPlan, ImportReport, ImportSelection, import_into};

use crate::settings::DuplicatePolicy;
use furchive_domain::{PoolKey, SearchQuery};
use furchive_engine::storage::SavedSearch;
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::io::Read;
use std::path::Path;

pub const MAX_SETTINGS_BYTES: usize = 1024 * 1024;
pub const MAX_DATABASE_BYTES: usize = 16 * 1024 * 1024;
pub const MAX_RECORDS: usize = 1000;
pub const MAX_TEXT_BYTES: usize = 4096;
pub const MAX_JSON_DEPTH: usize = 32;

/// Only a compatibility facade: reads settings, never writes or opens a store.
#[derive(Debug)]
pub struct LegacyPreview {
    pub searches: Vec<SavedSearch>,
    pub warnings: Vec<String>,
}

/// Optional compatible values, not executable Settings and never applied by import.
#[derive(Debug, Default, Clone, PartialEq, Serialize)]
pub struct SettingsPreview {
    pub filename_template: Option<String>,
    pub pool_filename_template: Option<String>,
    pub concurrent_downloads: Option<u8>,
    pub duplicates: Option<DuplicatePolicy>,
    pub gallery_scale: Option<f64>,
    pub save_metadata: Option<bool>,
    /// Mere text: never resolved, traversed, created, or used as a download root.
    pub directory_hint: Option<String>,
    /// Historical preference only, not authority to restore a session.
    pub requested_session_restore: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SessionPreview {
    pub query: SearchQuery,
    pub page: u32,
    pub pool: Option<PoolKey>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct PinPreview {
    pub key: PoolKey,
    pub name: String,
    pub post_count: u32,
}

/// No queue strings, destinations, URLs, errors, or credentials are retained.
#[derive(Debug, Default, Clone, PartialEq, Eq, Serialize)]
pub enum QueuePreview {
    #[default]
    Absent,
    Quarantined {
        records: usize,
    },
}

/// An immutable allowlisted snapshot. Raw input is not retained. The source root
/// is held privately (redacted in Debug) only to forbid writes into the old tree.
#[derive(Debug)]
pub struct Inventory {
    searches: Vec<SavedSearch>,
    pins: Vec<PinPreview>,
    settings: SettingsPreview,
    session: Option<SessionPreview>,
    queue: QueuePreview,
    warnings: Vec<String>,
    digest: String,
    source_root: SourceRoot,
}

#[derive(Clone)]
struct SourceRoot(std::path::PathBuf);
impl std::fmt::Debug for SourceRoot {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("<legacy root withheld>")
    }
}

impl Inventory {
    pub fn searches(&self) -> &[SavedSearch] {
        &self.searches
    }
    pub fn pins(&self) -> &[PinPreview] {
        &self.pins
    }
    pub fn settings(&self) -> &SettingsPreview {
        &self.settings
    }
    pub fn session(&self) -> Option<&SessionPreview> {
        self.session.as_ref()
    }
    pub fn queue(&self) -> &QueuePreview {
        &self.queue
    }
    pub fn warnings(&self) -> &[String] {
        &self.warnings
    }
    /// SHA256 of the canonical validated nonsecret inventory, not raw secret bytes.
    pub fn content_digest(&self) -> &str {
        &self.digest
    }
}

/// Caller supplies the legacy Furchive directory. Missing optional stores are OK;
/// present malformed stores fail the entire preview closed. No discovery/network.
pub fn preview(root: &Path) -> Result<Inventory, String> {
    let mut warnings =
        vec!["Legacy credentials are excluded; re-enter through the native vault later.".into()];
    let (searches, settings) = match read_optional(&root.join("settings.json"), MAX_SETTINGS_BYTES)?
    {
        Some(bytes) => json::settings(&bytes, &mut warnings)?,
        None => (Vec::new(), SettingsPreview::default()),
    };
    let pins = sqlite::pins(root, &mut warnings)?;
    let session = sqlite::session(root, &mut warnings)?;
    let queue = sqlite::queue(root)?;
    if matches!(queue, QueuePreview::Quarantined { .. }) {
        warnings.push(
            "Legacy downloads quarantined for manual recovery; no queued work imported or resumed."
                .into(),
        );
    }
    let digest = digest(
        &serde_json::to_vec(&(&searches, &pins, &settings, &session, &queue))
            .map_err(|_| "cannot encode legacy inventory")?,
    );
    let source_root = SourceRoot(
        root.canonicalize()
            .map_err(|_| "cannot identify legacy root")?,
    );
    Ok(Inventory {
        searches,
        pins,
        settings,
        session,
        queue,
        warnings,
        digest,
        source_root,
    })
}

pub fn read_settings(root: &Path) -> Result<LegacyPreview, String> {
    let bytes = read_optional(&root.join("settings.json"), MAX_SETTINGS_BYTES)?
        .ok_or("cannot open legacy settings")?;
    let mut warnings = Vec::new();
    let (searches, _) = json::settings(&bytes, &mut warnings)?;
    Ok(LegacyPreview { searches, warnings })
}

fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

/// Actual reads are capped, even if the file grows after open/stat. Symlinks and
/// nonregular leaves are refused; the explicitly supplied root must be quiescent.
fn read_optional(path: &Path, limit: usize) -> Result<Option<Vec<u8>>, String> {
    let metadata = match std::fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err("cannot inspect legacy input".into()),
    };
    if !metadata.file_type().is_file() {
        return Err("legacy input must be a regular file".into());
    }
    let file = std::fs::File::open(path).map_err(|_| "cannot open legacy input")?;
    bounded_read(file, limit).map(Some)
}

fn bounded_read(reader: impl Read, limit: usize) -> Result<Vec<u8>, String> {
    let mut bytes = Vec::new();
    reader
        .take(limit as u64 + 1)
        .read_to_end(&mut bytes)
        .map_err(|_| "cannot read legacy input")?;
    if bytes.len() > limit {
        return Err("legacy input exceeds byte limit".into());
    }
    Ok(bytes)
}

fn valid_text(text: &str) -> bool {
    !text.trim().is_empty() && text.len() <= MAX_TEXT_BYTES && !text.chars().any(char::is_control)
}
