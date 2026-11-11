use serde::{Deserialize, Serialize};

/// String representation keeps unknown upstream category keys unchanged.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[serde(from = "String", into = "String")]
pub enum TagCategory {
    General,
    Artist,
    Contributor,
    Copyright,
    Character,
    Species,
    Invalid,
    Meta,
    Lore,
    Unknown(String),
}

impl From<String> for TagCategory {
    fn from(value: String) -> Self {
        match value.as_str() {
            "general" => Self::General,
            "artist" => Self::Artist,
            "contributor" => Self::Contributor,
            "copyright" => Self::Copyright,
            "character" => Self::Character,
            "species" => Self::Species,
            "invalid" => Self::Invalid,
            "meta" => Self::Meta,
            "lore" => Self::Lore,
            _ => Self::Unknown(value),
        }
    }
}

impl From<TagCategory> for String {
    fn from(value: TagCategory) -> Self {
        match value {
            TagCategory::General => "general".into(),
            TagCategory::Artist => "artist".into(),
            TagCategory::Contributor => "contributor".into(),
            TagCategory::Copyright => "copyright".into(),
            TagCategory::Character => "character".into(),
            TagCategory::Species => "species".into(),
            TagCategory::Invalid => "invalid".into(),
            TagCategory::Meta => "meta".into(),
            TagCategory::Lore => "lore".into(),
            TagCategory::Unknown(value) => value,
        }
    }
}

impl TagCategory {
    pub fn from_e621_id(id: u32) -> Self {
        match id {
            0 => Self::General,
            1 => Self::Artist,
            2 => Self::Contributor,
            3 => Self::Copyright,
            4 => Self::Character,
            5 => Self::Species,
            6 => Self::Invalid,
            7 => Self::Meta,
            8 => Self::Lore,
            _ => Self::Unknown(id.to_string()),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum MediaKind {
    Image,
    Animation,
    Video,
    Unknown(String),
}
