//! Host-selected explicit migration preview/confirmation, no browser path authority.
use crate::legacy::{self, ImportReport, ImportSelection, Inventory};
use furchive_engine::storage::SavedSearch;
use serde::Serialize;
use std::{path::Path, sync::Mutex};
#[derive(Serialize)]
pub struct Preview {
    pub digest: String,
    pub searches: Vec<SavedSearch>,
    pub pins: Vec<legacy::PinPreview>,
    pub warnings: Vec<String>,
    pub queue: legacy::QueuePreview,
}
#[derive(Default)]
pub struct LegacySession {
    snapshot: Mutex<Option<Inventory>>,
}
impl LegacySession {
    pub fn new() -> Self {
        Self::default()
    }
    pub fn select_directory(&self, root: &Path) -> Result<Preview, String> {
        // Revoke the prior preview before reading a newly selected directory, even if invalid.
        let mut slot = self
            .snapshot
            .lock()
            .map_err(|_| "legacy preview lock poisoned")?;
        *slot = None;
        let inventory = legacy::preview(root)?;
        let preview = Preview {
            digest: inventory.content_digest().into(),
            searches: inventory.searches().to_vec(),
            pins: inventory.pins().to_vec(),
            warnings: inventory.warnings().to_vec(),
            queue: inventory.queue().clone(),
        };
        *slot = Some(inventory);
        Ok(preview)
    }
    pub fn clear(&self) -> Result<(), String> {
        *self
            .snapshot
            .lock()
            .map_err(|_| "legacy preview lock poisoned")? = None;
        Ok(())
    }
    pub fn confirm(
        &self,
        digest: &str,
        search_ids: Vec<String>,
        pool_ids: Vec<String>,
        destination: &Path,
    ) -> Result<ImportReport, String> {
        legacy::import_into(self.confirmed(digest, search_ids, pool_ids)?, destination)
    }
    pub fn confirmed(
        &self,
        digest: &str,
        search_ids: Vec<String>,
        pool_ids: Vec<String>,
    ) -> Result<legacy::ConfirmedImport, String> {
        let inventory = self
            .snapshot
            .lock()
            .map_err(|_| "legacy preview lock poisoned")?
            .take()
            .ok_or("select and preview a legacy directory first")?;
        let plan = inventory
            .select(ImportSelection {
                search_ids,
                pool_ids,
            })?
            .confirm(digest)?;
        Ok(plan)
    }
}
