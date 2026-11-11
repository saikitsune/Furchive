use crate::{IdentityError, PostKey};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(transparent)]
pub struct PoolKey(PostKey);

impl PoolKey {
    pub fn new(source: impl Into<String>, id: impl Into<String>) -> Result<Self, IdentityError> {
        Ok(Self(PostKey::new(source, id)?))
    }
    pub fn source(&self) -> &str {
        self.0.source()
    }
    pub fn id(&self) -> &str {
        self.0.id()
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Pool {
    pub key: PoolKey,
    pub name: String,
    pub description: Option<String>,
    pub active: bool,
    pub category: Option<String>,
    pub created_at: Option<String>,
    pub updated_at: Option<String>,
    pub fetched_at: Option<i64>,
    /// Full authoritative upstream order, including uncached/unavailable posts.
    pub post_ids: Vec<String>,
}

impl Pool {
    pub fn new(key: PoolKey, name: String) -> Self {
        Self {
            key,
            name,
            description: None,
            active: true,
            category: None,
            created_at: None,
            updated_at: None,
            fetched_at: None,
            post_ids: Vec::new(),
        }
    }
}
