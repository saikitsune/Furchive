pub mod endpoints;
pub use endpoints::{TagSuggestion, map_pool, map_suggestions};
pub mod account;
pub use account::Account;
pub mod client;
pub use client::E621Client;
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct VerifiedIdentity {
    pub id: u64,
    pub name: String,
}
#[cfg(test)]
mod tests {
    #[test]
    fn maps_contributor_unknown_categories_description_and_null_file_without_loss() {
        let value = serde_json::json!({"id":12,"description":"original description","rating":"s","tags":{"artist":["maker"],"contributor":["voice"],"future_category":["future"]},"file":{"url":null,"ext":"webm","size":21,"width":320,"height":200},"preview":{"url":"https://static1.e621.net/test.jpg"},"score":{"total":4},"fav_count":3});
        let item = super::map_post(&value).unwrap();
        assert_eq!(item.description, "original description");
        assert_eq!(
            item.tags
                .get(&furchive_domain::TagCategory::Contributor)
                .unwrap(),
            &["voice"]
        );
        assert!(item.full_url.is_none());
        assert!(
            item.tags
                .contains_key(&furchive_domain::TagCategory::Unknown(
                    "future_category".into()
                ))
        );
    }
}
#[derive(Debug, serde::Serialize, serde::Deserialize)]
pub struct SearchPage {
    pub items: Vec<furchive_domain::MediaItem>,
    pub next_page: Option<u32>,
    pub raw_count: usize,
}
pub fn parse_search_page(
    value: serde_json::Value,
    query: &furchive_domain::SearchQuery,
    page: u32,
    limit: usize,
) -> Result<SearchPage, String> {
    let posts = value["posts"].as_array().ok_or("missing posts array")?;
    let raw_count = posts.len();
    let items = posts
        .iter()
        .map(map_post)
        .collect::<Result<Vec<_>, _>>()?
        .into_iter()
        .filter(|p| query.ratings.accepts(&p.rating))
        .collect();
    Ok(SearchPage {
        items,
        next_page: if raw_count >= limit {
            page.checked_add(1)
        } else {
            None
        },
        raw_count,
    })
}
pub fn map_post(value: &serde_json::Value) -> Result<furchive_domain::MediaItem, String> {
    use furchive_domain::{MediaItem, MediaKind, PostKey, Rating, TagCategory};
    let id = value["id"]
        .as_u64()
        .filter(|id| *id > 0)
        .ok_or("invalid post ID")?;
    let mut item = MediaItem::new(PostKey::new("e621", id.to_string()).map_err(|e| e.to_string())?);
    item.title = format!("Post {id}");
    item.description = value["description"].as_str().unwrap_or_default().into();
    item.rating = match value["rating"].as_str().unwrap_or_default() {
        "s" => Rating::Safe,
        "q" => Rating::Questionable,
        "e" => Rating::Explicit,
        other => Rating::Unknown(other.into()),
    };
    if let Some(tags) = value["tags"].as_object() {
        for (category, tags) in tags {
            let tags = tags
                .as_array()
                .ok_or("invalid tags")?
                .iter()
                .map(|t| {
                    t.as_str()
                        .map(str::to_owned)
                        .ok_or("invalid tag".to_string())
                })
                .collect::<Result<Vec<_>, _>>()?;
            item.tags.insert(TagCategory::from(category.clone()), tags);
        }
    }
    item.artists = item
        .tags
        .get(&TagCategory::Artist)
        .cloned()
        .unwrap_or_default();
    item.full_url = value["file"]["url"]
        .as_str()
        .filter(|s| !s.is_empty())
        .map(str::to_owned);
    item.preview_url = value["preview"]["url"]
        .as_str()
        .filter(|s| !s.is_empty())
        .map(str::to_owned);
    item.source_url = Some(format!("https://e621.net/posts/{id}"));
    item.file_extension = value["file"]["ext"].as_str().map(str::to_owned);
    item.kind = match item.file_extension.as_deref() {
        Some("webm" | "mp4") => MediaKind::Video,
        Some("gif" | "apng") => MediaKind::Animation,
        Some("png" | "jpg" | "jpeg" | "webp") => MediaKind::Image,
        ext => MediaKind::Unknown(ext.unwrap_or_default().into()),
    };
    item.created_at = value["created_at"].as_str().map(str::to_owned);
    item.file_size_bytes = value["file"]["size"].as_u64();
    item.file_md5 = match value["file"]["md5"].as_str() {
        Some(hash) => {
            if hash.len() != 32
                || !hash
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            {
                return Err("invalid upstream file checksum".into());
            }
            Some(hash.into())
        }
        None => None,
    };
    item.width = value["file"]["width"]
        .as_u64()
        .and_then(|x| u32::try_from(x).ok());
    item.height = value["file"]["height"]
        .as_u64()
        .and_then(|x| u32::try_from(x).ok());
    item.score = value["score"]["total"].as_i64().unwrap_or_default();
    item.favorite_count = value["fav_count"].as_u64().unwrap_or_default();
    item.fetched_at = Some(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|e| e.to_string())?
            .as_secs() as i64,
    );
    Ok(item)
}
