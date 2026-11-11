use serde::{Deserialize, Serialize};
use thiserror::Error;

#[derive(Debug, Error, Clone, PartialEq, Eq)]
#[error("source and id must be nonempty and contain no whitespace/control characters")]
pub struct IdentityError;

#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(try_from = "KeyParts")]
pub struct PostKey {
    source: String,
    id: String,
}

#[derive(Deserialize)]
struct KeyParts {
    source: String,
    id: String,
}

impl TryFrom<KeyParts> for PostKey {
    type Error = IdentityError;
    fn try_from(value: KeyParts) -> Result<Self, Self::Error> {
        Self::new(value.source, value.id)
    }
}

impl PostKey {
    pub fn new(source: impl Into<String>, id: impl Into<String>) -> Result<Self, IdentityError> {
        let (source, id) = (source.into(), id.into());
        if [&source, &id]
            .iter()
            .any(|s| s.is_empty() || s.chars().any(|c| c.is_whitespace() || c.is_control()))
        {
            return Err(IdentityError);
        }
        Ok(Self { source, id })
    }
    pub fn source(&self) -> &str {
        &self.source
    }
    pub fn id(&self) -> &str {
        &self.id
    }
}
