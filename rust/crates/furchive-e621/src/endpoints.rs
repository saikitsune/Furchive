use furchive_domain::{Pool, PoolKey, TagCategory};
use serde::{Deserialize, Serialize};
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TagSuggestion {
    pub name: String,
    pub category: TagCategory,
    pub post_count: u64,
}
pub fn map_suggestions(values: serde_json::Value) -> Result<Vec<TagSuggestion>, String> {
    values
        .as_array()
        .ok_or("missing tags array")?
        .iter()
        .map(|v| {
            Ok(TagSuggestion {
                name: v["name"].as_str().ok_or("missing tag name")?.into(),
                category: TagCategory::from_e621_id(
                    v["category"]
                        .as_u64()
                        .and_then(|n| u32::try_from(n).ok())
                        .ok_or("missing tag category")?,
                ),
                post_count: v["post_count"].as_u64().unwrap_or_default(),
            })
        })
        .collect()
}
pub fn map_pool(v: &serde_json::Value) -> Result<Pool, String> {
    let id = v["id"]
        .as_u64()
        .filter(|n| *n > 0)
        .ok_or("invalid pool ID")?;
    let mut p = Pool::new(
        PoolKey::new("e621", id.to_string()).map_err(|e| e.to_string())?,
        v["name"].as_str().ok_or("missing pool name")?.into(),
    );
    p.description = v["description"].as_str().map(str::to_owned);
    p.category = v["category"].as_str().map(str::to_owned);
    p.active = v["is_active"].as_bool().unwrap_or(true);
    p.created_at = v["created_at"].as_str().map(str::to_owned);
    p.updated_at = v["updated_at"].as_str().map(str::to_owned);
    p.post_ids = v["post_ids"]
        .as_array()
        .ok_or("missing pool order")?
        .iter()
        .map(|v| {
            v.as_u64()
                .filter(|n| *n > 0)
                .map(|n| n.to_string())
                .ok_or("invalid pool post ID".into())
        })
        .collect::<Result<_, String>>()?;
    Ok(p)
}
