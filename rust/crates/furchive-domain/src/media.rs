use crate::{MediaKind, PostKey, Rating, TagCategory};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MediaItem {
    pub key: PostKey,
    pub tags: std::collections::BTreeMap<TagCategory, Vec<String>>,
    pub kind: MediaKind,
    pub title: String,
    pub description: String,
    pub artists: Vec<String>,
    pub preview_url: Option<String>,
    pub full_url: Option<String>,
    pub source_url: Option<String>,
    pub rating: Rating,
    pub created_at: Option<String>,
    /// Unix seconds, UTC; assigned by the fetching boundary, not storage.
    pub fetched_at: Option<i64>,
    pub score: i64,
    pub favorite_count: u64,
    pub file_extension: Option<String>,
    pub file_size_bytes: Option<u64>,
    #[serde(default)]
    pub file_md5: Option<String>,
    pub width: Option<u32>,
    pub height: Option<u32>,
}

impl MediaItem {
    pub fn new(key: PostKey) -> Self {
        Self {
            key,
            tags: Default::default(),
            kind: MediaKind::Unknown(String::new()),
            title: String::new(),
            description: String::new(),
            artists: Vec::new(),
            preview_url: None,
            full_url: None,
            source_url: None,
            rating: Rating::Unknown(String::new()),
            created_at: None,
            fetched_at: None,
            score: 0,
            favorite_count: 0,
            file_extension: None,
            file_size_bytes: None,
            file_md5: None,
            width: None,
            height: None,
        }
    }
}
