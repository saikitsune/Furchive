use super::{Result, StorageError};
use rusqlite::{Connection, TransactionBehavior};
use std::time::Duration;

pub const SCHEMA_VERSION: u32 = 2;
pub const CACHE_APPLICATION_ID: i64 = 0x46554348;
pub const STATE_APPLICATION_ID: i64 = 0x46555354;

pub(super) fn initialize(connection: &mut Connection, application_id: i64) -> Result<()> {
    connection.busy_timeout(Duration::from_secs(5))?;
    connection.pragma_update(None, "foreign_keys", true)?;
    // One immediate transaction serializes concurrent openers and rolls back the
    // entire upgrade, including the version marker, if any step fails.
    let tx = connection.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let existing_id: i64 = tx.query_row("PRAGMA application_id", [], |r| r.get(0))?;
    let version: u32 = tx.query_row("PRAGMA user_version", [], |r| r.get(0))?;
    let objects: u32 = tx.query_row(
        "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'",
        [],
        |r| r.get(0),
    )?;
    if version > SCHEMA_VERSION {
        return Err(StorageError::Invalid(
            "newer schema requires a newer application".into(),
        ));
    }
    if existing_id != application_id && !(existing_id == 0 && version == 0 && objects == 0) {
        return Err(StorageError::Invalid(
            "foreign or legacy database; explicit importer required".into(),
        ));
    }
    if version == 0 && objects != 0 {
        return Err(StorageError::Invalid(
            "unversioned nonempty database".into(),
        ));
    }
    let steps = if application_id == CACHE_APPLICATION_ID {
        [
            include_str!("../../migrations/cache_0001.sql"),
            include_str!("../../migrations/cache_0002.sql"),
        ]
    } else {
        [
            include_str!("../../migrations/state_0001.sql"),
            include_str!("../../migrations/state_0002.sql"),
        ]
    };
    for (index, sql) in steps.iter().enumerate().skip(version as usize) {
        tx.execute_batch(sql)?;
        let next_version = u32::try_from(index + 1)
            .map_err(|_| StorageError::Invalid("schema version overflow".into()))?;
        tx.pragma_update(None, "user_version", next_version)?;
    }
    tx.pragma_update(None, "application_id", application_id)?;
    tx.commit()?;
    // WAL is appropriate only for local files. open() documents that boundary.
    // SQLite reports 'memory' for in-memory connections, which is also valid.
    connection.pragma_update(None, "journal_mode", "WAL")?;
    connection.pragma_update(None, "synchronous", "FULL")?;
    Ok(())
}
