use super::{Result, StorageError};
use furchive_domain::{PoolKey, SearchQuery};
use rusqlite::{params, Connection};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SavedSearch {
    pub id: String,
    pub name: String,
    pub query: SearchQuery,
}

pub struct StateStore {
    pub(super) connection: Connection,
}

impl StateStore {
    pub fn in_memory() -> Result<Self> {
        Self::from_connection(Connection::open_in_memory()?)
    }
    /// Local disk only (WAL); caller creates/authorizes the parent directory.
    pub fn open(path: impl AsRef<std::path::Path>) -> Result<Self> {
        Self::from_connection(Connection::open(path)?)
    }
    pub fn from_connection(mut connection: Connection) -> Result<Self> {
        super::migrations::initialize(&mut connection, super::STATE_APPLICATION_ID)?;
        Ok(Self { connection })
    }
    pub fn save_search(&mut self, search: &SavedSearch) -> Result<()> {
        if search.id.trim().is_empty() || search.name.trim().is_empty() {
            return Err(StorageError::Invalid("empty saved search id/name".into()));
        }
        search
            .query
            .e621_tags()
            .map_err(|e| StorageError::Invalid(e.to_string()))?;
        self.connection.execute("INSERT INTO saved_searches(id,name,record_version,query_json) VALUES(?1,?2,1,?3) ON CONFLICT(id) DO UPDATE SET name=excluded.name,record_version=excluded.record_version,query_json=excluded.query_json", params![search.id, search.name, serde_json::to_string(&search.query)?])?;
        Ok(())
    }
    pub fn saved_searches(&self) -> Result<Vec<SavedSearch>> {
        let mut stmt = self
            .connection
            .prepare("SELECT id,name,record_version,query_json FROM saved_searches ORDER BY id")?;
        let rows = stmt.query_map([], |r| {
            Ok((
                r.get::<_, String>(0)?,
                r.get::<_, String>(1)?,
                r.get::<_, u32>(2)?,
                r.get::<_, String>(3)?,
            ))
        })?;
        let mut searches = Vec::new();
        for row in rows {
            let (id, name, version, json) = row?;
            if version != 1 {
                return Err(StorageError::Invalid(
                    "unsupported saved search record version".into(),
                ));
            }
            let value: serde_json::Value = serde_json::from_str(&json)?;
            if !value.is_object() {
                return Err(StorageError::Invalid(
                    "saved query must be a JSON object".into(),
                ));
            }
            let query: SearchQuery = serde_json::from_value(value)?;
            query
                .e621_tags()
                .map_err(|e| StorageError::Invalid(e.to_string()))?;
            searches.push(SavedSearch { id, name, query });
        }
        Ok(searches)
    }
    /// Explicitly flush schema/header markers before the separate constrained importer opens this DB.
    pub fn checkpoint(&self) -> Result<()> {
        self.connection
            .execute_batch("PRAGMA wal_checkpoint(FULL);")?;
        Ok(())
    }
    pub fn delete_search(&mut self, id: &str) -> Result<()> {
        self.connection
            .execute("DELETE FROM saved_searches WHERE id=?1", [id])?;
        Ok(())
    }
    pub fn set_pin(&mut self, key: &PoolKey, pinned: bool) -> Result<()> {
        if pinned {
            self.connection.execute("INSERT INTO pinned_pools(source,pool_id) VALUES(?1,?2) ON CONFLICT(source,pool_id) DO NOTHING", params![key.source(), key.id()])?;
        } else {
            self.connection.execute(
                "DELETE FROM pinned_pools WHERE source=?1 AND pool_id=?2",
                params![key.source(), key.id()],
            )?;
        }
        Ok(())
    }
    pub fn pinned_pools(&self) -> Result<Vec<PoolKey>> {
        let mut stmt = self
            .connection
            .prepare("SELECT source,pool_id FROM pinned_pools ORDER BY source,pool_id")?;
        let rows = stmt.query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))?;
        rows.map(|r| {
            let (source, id) = r?;
            Ok(PoolKey::new(source, id)?)
        })
        .collect()
    }
}
