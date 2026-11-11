mod migrations;
mod state;
pub use migrations::{CACHE_APPLICATION_ID, SCHEMA_VERSION, STATE_APPLICATION_ID};
pub use state::{SavedSearch, StateStore};

use furchive_domain::{MediaItem, Pool, PoolKey, PostKey};
use rusqlite::{params, Connection, OptionalExtension};
use std::collections::HashSet;
use thiserror::Error;

#[derive(Debug, Error)]
pub enum StorageError {
    #[error("invalid snapshot: {0}")]
    Invalid(String),
    #[error(transparent)]
    Identity(#[from] furchive_domain::IdentityError),
    #[error("SQLite storage error: {0}")]
    Sqlite(#[from] rusqlite::Error),
    #[error("invalid stored JSON: {0}")]
    Json(#[from] serde_json::Error),
}

pub type Result<T> = std::result::Result<T, StorageError>;

fn decode_post(json: &str, key: &PostKey) -> Result<MediaItem> {
    let post: MediaItem = serde_json::from_str(json)?;
    if &post.key != key {
        return Err(StorageError::Invalid(
            "stored post identity mismatch".into(),
        ));
    }
    Ok(post)
}

pub struct CacheStore {
    connection: Connection,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PoolEntry {
    pub key: PostKey,
    /// Zero-based authoritative position; do not compact uncached members.
    pub position: u32,
    pub post: Option<MediaItem>,
}

impl CacheStore {
    pub fn in_memory() -> Result<Self> {
        Self::from_connection(Connection::open_in_memory()?)
    }
    /// Local disk only (WAL); caller creates/authorizes the parent directory.
    pub fn open(path: impl AsRef<std::path::Path>) -> Result<Self> {
        Self::from_connection(Connection::open(path)?)
    }
    pub fn from_connection(mut connection: Connection) -> Result<Self> {
        migrations::initialize(&mut connection, CACHE_APPLICATION_ID)?;
        Ok(Self { connection })
    }
    /// Clears discardable metadata only. User state belongs to StateStore.
    pub fn clear(&mut self) -> Result<()> {
        let tx = self.connection.transaction()?;
        tx.execute_batch("DELETE FROM pool_posts; DELETE FROM pools; DELETE FROM posts;")?;
        tx.commit()?;
        Ok(())
    }
    pub fn upsert_posts(&mut self, posts: &[MediaItem]) -> Result<()> {
        let tx = self.connection.transaction()?;
        for post in posts {
            tx.execute("INSERT INTO posts(source,post_id,data_json,fetched_at) VALUES(?1,?2,?3,?4) ON CONFLICT(source,post_id) DO UPDATE SET data_json=excluded.data_json,fetched_at=excluded.fetched_at", params![post.key.source(), post.key.id(), serde_json::to_string(post)?, post.fetched_at])?;
        }
        tx.commit()?;
        Ok(())
    }
    pub fn reconcile_pool(&mut self, pool: &Pool) -> Result<()> {
        let mut ids = HashSet::new();
        for id in &pool.post_ids {
            PostKey::new(pool.key.source(), id)?;
            if !ids.insert(id) {
                return Err(StorageError::Invalid("duplicate pool member".into()));
            }
        }
        let tx = self.connection.transaction()?;
        tx.execute("INSERT INTO pools(source,pool_id,data_json,fetched_at) VALUES(?1,?2,?3,?4) ON CONFLICT(source,pool_id) DO UPDATE SET data_json=excluded.data_json,fetched_at=excluded.fetched_at", params![pool.key.source(), pool.key.id(), serde_json::to_string(pool)?, pool.fetched_at])?;
        tx.execute(
            "DELETE FROM pool_posts WHERE source=?1 AND pool_id=?2",
            params![pool.key.source(), pool.key.id()],
        )?;
        for (position, id) in pool.post_ids.iter().enumerate() {
            tx.execute(
                "INSERT INTO pool_posts(source,pool_id,post_id,position) VALUES(?1,?2,?3,?4)",
                params![pool.key.source(), pool.key.id(), id, position as i64],
            )?;
        }
        tx.commit()?;
        Ok(())
    }
    pub fn get_pool(&self, key: &PoolKey) -> Result<Option<Pool>> {
        let json: Option<String> = self
            .connection
            .query_row(
                "SELECT data_json FROM pools WHERE source=?1 AND pool_id=?2",
                params![key.source(), key.id()],
                |r| r.get(0),
            )
            .optional()?;
        json.map(|j| {
            let pool: Pool = serde_json::from_str(&j)?;
            if &pool.key != key {
                return Err(StorageError::Invalid(
                    "stored pool identity mismatch".into(),
                ));
            }
            Ok(pool)
        })
        .transpose()
    }
    /// Missing cache entries retain their authoritative positions and identities.
    pub fn pool_entries(&self, key: &PoolKey) -> Result<Vec<PoolEntry>> {
        let mut statement = self.connection.prepare("SELECT m.post_id,m.position,p.data_json FROM pool_posts m LEFT JOIN posts p ON p.source=m.source AND p.post_id=m.post_id WHERE m.source=?1 AND m.pool_id=?2 ORDER BY m.position")?;
        let rows = statement.query_map(params![key.source(), key.id()], |row| {
            Ok((
                row.get::<_, String>(0)?,
                row.get::<_, u32>(1)?,
                row.get::<_, Option<String>>(2)?,
            ))
        })?;
        let mut entries = Vec::new();
        for row in rows {
            let (id, position, json) = row?;
            let key = PostKey::new(key.source(), id)?;
            entries.push(PoolEntry {
                post: json.map(|j| decode_post(&j, &key)).transpose()?,
                key,
                position,
            });
        }
        Ok(entries)
    }
    pub fn get_post(&self, key: &furchive_domain::PostKey) -> Result<Option<MediaItem>> {
        let json: Option<String> = self
            .connection
            .query_row(
                "SELECT data_json FROM posts WHERE source=?1 AND post_id=?2",
                params![key.source(), key.id()],
                |r| r.get(0),
            )
            .optional()?;
        json.map(|j| decode_post(&j, key)).transpose()
    }
}
