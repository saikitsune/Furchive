use super::Inventory;
use furchive_engine::storage::{SCHEMA_VERSION, STATE_APPLICATION_ID};
use rusqlite::{Connection, OpenFlags, TransactionBehavior, params};
use std::collections::HashSet;
use std::path::Path;

#[derive(Debug, Default)]
pub struct ImportSelection {
    pub search_ids: Vec<String>,
    /// Decimal e621 pool IDs as shown in the inventory (no other sources).
    pub pool_ids: Vec<String>,
}

#[derive(Debug)]
pub struct ImportPlan {
    searches: Vec<furchive_engine::storage::SavedSearch>,
    pins: Vec<furchive_domain::PoolKey>,
    digest: String,
    source_root: super::SourceRoot,
}

/// Cannot be constructed except by explicit selection followed by confirmation.
#[derive(Debug)]
pub struct ConfirmedImport(ImportPlan);

#[derive(Debug, Default, PartialEq, Eq, serde::Serialize)]
pub struct ImportReport {
    pub imported_searches: usize,
    pub imported_pins: usize,
    pub skipped_searches: usize,
    pub skipped_pins: usize,
}

impl Inventory {
    pub fn select(&self, selection: ImportSelection) -> Result<ImportPlan, String> {
        if selection.search_ids.len() > super::MAX_RECORDS
            || selection.pool_ids.len() > super::MAX_RECORDS
        {
            return Err("legacy selection record limit".into());
        }
        let mut searches = Vec::new();
        let mut pins = Vec::new();
        let mut seen = HashSet::new();
        for id in selection.search_ids {
            if !seen.insert(id.clone()) {
                return Err("duplicate search selection".into());
            }
            searches.push(
                self.searches
                    .iter()
                    .find(|s| s.id == id)
                    .ok_or("unknown search selection")?
                    .clone(),
            );
        }
        seen.clear();
        for id in selection.pool_ids {
            if !seen.insert(id.clone()) {
                return Err("duplicate pin selection".into());
            }
            pins.push(
                self.pins
                    .iter()
                    .find(|p| p.key.id() == id)
                    .ok_or("unknown pin selection")?
                    .key
                    .clone(),
            );
        }
        if searches.is_empty() && pins.is_empty() {
            return Err("legacy selection is empty".into());
        }
        Ok(ImportPlan {
            searches,
            pins,
            digest: self.digest.clone(),
            source_root: self.source_root.clone(),
        })
    }
}

impl ImportPlan {
    /// The caller must explicitly confirm the digest displayed with the preview.
    /// Consumes the plan; no source files are re-read after confirmation.
    pub fn confirm(self, displayed_digest: &str) -> Result<ConfirmedImport, String> {
        if self.digest != displayed_digest {
            return Err("legacy confirmation digest mismatch".into());
        }
        Ok(ConfirmedImport(self))
    }
}

/// Writes only an already initialized, caller-authorized Rust StateStore file.
/// One IMMEDIATE transaction; no migration/creation, no UPDATE/REPLACE/IGNORE.
/// Any admitted write failure rolls back all rows (including idempotency receipts).
pub fn import_into(confirmed: ConfirmedImport, destination: &Path) -> Result<ImportReport, String> {
    let destination = destination
        .canonicalize()
        .map_err(|_| "destination must be an existing authorized state database")?;
    if destination.starts_with(&confirmed.0.source_root.0) {
        return Err("destination must be outside the preserved legacy tree".into());
    }
    // Inspect a fixed header before a writable SQLite open can checkpoint/recover
    // or create sidecars in a foreign/legacy database. Newly initialized stores
    // must have their markers checkpointed by closing StateStore first.
    let mut header = [0u8; 100];
    use std::io::Read;
    std::fs::File::open(&destination)
        .and_then(|mut f| f.read_exact(&mut header))
        .map_err(|_| "destination state header unavailable")?;
    if &header[..16] != b"SQLite format 3\0"
        || u32::from_be_bytes(
            header[60..64]
                .try_into()
                .map_err(|_| "invalid state header")?,
        ) != SCHEMA_VERSION
        || u32::from_be_bytes(
            header[68..72]
                .try_into()
                .map_err(|_| "invalid state header")?,
        ) as i64
            != STATE_APPLICATION_ID
    {
        return Err("destination is not a checkpointed Rust state database".into());
    }
    import(confirmed.0, &destination)
        .map_err(|_| "legacy import failed; transaction rolled back".into())
}

fn import(plan: ImportPlan, destination: &Path) -> rusqlite::Result<ImportReport> {
    let mut conn = Connection::open_with_flags(
        destination,
        OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_NO_MUTEX,
    )?;
    conn.busy_timeout(std::time::Duration::from_secs(2))?;
    conn.pragma_update(None, "trusted_schema", false)?;
    conn.pragma_update(None, "foreign_keys", true)?;
    conn.pragma_update(None, "synchronous", "FULL")?;
    let tx = conn.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let app: i64 = tx.query_row("PRAGMA application_id", [], |r| r.get(0))?;
    let version: u32 = tx.query_row("PRAGMA user_version", [], |r| r.get(0))?;
    if app != STATE_APPLICATION_ID || version != SCHEMA_VERSION {
        return Err(rusqlite::Error::InvalidQuery);
    }
    // The ledger binds item identity, not an entire raw legacy source. It prevents
    // reimport after a user edits/deletes an imported row. It never stores secrets.
    tx.execute_batch("CREATE TABLE IF NOT EXISTS legacy_import_v1 (kind TEXT NOT NULL, item_id TEXT NOT NULL, PRIMARY KEY(kind,item_id)) WITHOUT ROWID;")?;
    let mut report = ImportReport::default();
    for s in plan.searches {
        let imported: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM legacy_import_v1 WHERE kind='search' AND item_id=?1)",
            [&s.id],
            |r| r.get(0),
        )?;
        let collision: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM saved_searches WHERE id=?1 OR lower(name)=lower(?2))",
            params![s.id, s.name],
            |r| r.get(0),
        )?;
        if imported || collision {
            report.skipped_searches += 1;
            continue;
        }
        let query = serde_json::to_string(&s.query).map_err(|_| rusqlite::Error::InvalidQuery)?;
        if tx.execute(
            "INSERT INTO saved_searches(id,name,record_version,query_json) VALUES(?1,?2,1,?3)",
            params![s.id, s.name, query],
        )? != 1
            || tx.execute(
                "INSERT INTO legacy_import_v1(kind,item_id) VALUES('search',?1)",
                [&s.id],
            )? != 1
        {
            return Err(rusqlite::Error::InvalidQuery);
        }
        report.imported_searches += 1;
    }
    for pin in plan.pins {
        let id = pin.id();
        let imported: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM legacy_import_v1 WHERE kind='e621-pin' AND item_id=?1)",
            [id],
            |r| r.get(0),
        )?;
        let collision: bool = tx.query_row(
            "SELECT EXISTS(SELECT 1 FROM pinned_pools WHERE source='e621' AND pool_id=?1)",
            [id],
            |r| r.get(0),
        )?;
        if imported || collision {
            report.skipped_pins += 1;
            continue;
        }
        if tx.execute(
            "INSERT INTO pinned_pools(source,pool_id) VALUES('e621',?1)",
            [id],
        )? != 1
            || tx.execute(
                "INSERT INTO legacy_import_v1(kind,item_id) VALUES('e621-pin',?1)",
                [id],
            )? != 1
        {
            return Err(rusqlite::Error::InvalidQuery);
        }
        report.imported_pins += 1;
    }
    tx.commit()?;
    Ok(report)
}
