use super::{MAX_JSON_DEPTH, MAX_RECORDS, MAX_TEXT_BYTES, SettingsPreview, digest, valid_text};
use crate::settings::DuplicatePolicy;
use furchive_domain::{FilenameTemplate, Rating, RatingFilter, SearchQuery};
use furchive_engine::storage::SavedSearch;
use serde_json::Value;

pub(super) fn parse(bytes: &[u8]) -> Result<Value, String> {
    // Check nesting before recursive serde parsing, including ignored fields.
    let (mut depth, mut quoted, mut escape) = (0usize, false, false);
    for &b in bytes {
        if quoted {
            if escape {
                escape = false;
            } else if b == b'\\' {
                escape = true;
            } else if b == b'"' {
                quoted = false;
            }
        } else {
            match b {
                b'"' => quoted = true,
                b'{' | b'[' => {
                    depth += 1;
                    if depth > MAX_JSON_DEPTH {
                        return Err("legacy JSON depth limit".into());
                    }
                }
                b'}' | b']' => {
                    depth = depth.saturating_sub(1);
                }
                _ => {}
            }
        }
    }
    let mut de = serde_json::Deserializer::from_slice(bytes);
    let value = <UniqueValue as serde::Deserialize>::deserialize(&mut de)
        .map_err(|_| "legacy JSON invalid or ambiguous")?;
    de.end().map_err(|_| "legacy JSON trailing content")?;
    Ok(value.0)
}

/// Deserialize recursively while refusing duplicate keys, even in ignored fields.
struct UniqueValue(Value);
impl<'de> serde::Deserialize<'de> for UniqueValue {
    fn deserialize<D: serde::Deserializer<'de>>(de: D) -> Result<Self, D::Error> {
        struct Visitor;
        impl<'de> serde::de::Visitor<'de> for Visitor {
            type Value = UniqueValue;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("unambiguous JSON")
            }
            fn visit_unit<E: serde::de::Error>(self) -> Result<Self::Value, E> {
                Ok(UniqueValue(Value::Null))
            }
            fn visit_bool<E: serde::de::Error>(self, v: bool) -> Result<Self::Value, E> {
                Ok(UniqueValue(v.into()))
            }
            fn visit_i64<E: serde::de::Error>(self, v: i64) -> Result<Self::Value, E> {
                Ok(UniqueValue(v.into()))
            }
            fn visit_u64<E: serde::de::Error>(self, v: u64) -> Result<Self::Value, E> {
                Ok(UniqueValue(v.into()))
            }
            fn visit_f64<E: serde::de::Error>(self, v: f64) -> Result<Self::Value, E> {
                serde_json::Number::from_f64(v)
                    .map(|v| UniqueValue(v.into()))
                    .ok_or_else(|| E::custom("invalid JSON number"))
            }
            fn visit_str<E: serde::de::Error>(self, v: &str) -> Result<Self::Value, E> {
                Ok(UniqueValue(v.into()))
            }
            fn visit_string<E: serde::de::Error>(self, v: String) -> Result<Self::Value, E> {
                Ok(UniqueValue(v.into()))
            }
            fn visit_seq<A: serde::de::SeqAccess<'de>>(
                self,
                mut seq: A,
            ) -> Result<Self::Value, A::Error> {
                let mut values = Vec::new();
                while let Some(v) = seq.next_element::<UniqueValue>()? {
                    values.push(v.0);
                }
                Ok(UniqueValue(values.into()))
            }
            fn visit_map<A: serde::de::MapAccess<'de>>(
                self,
                mut map: A,
            ) -> Result<Self::Value, A::Error> {
                let mut values = serde_json::Map::new();
                while let Some(key) = map.next_key::<String>()? {
                    if values.contains_key(&key) {
                        return Err(serde::de::Error::custom("duplicate JSON key"));
                    }
                    values.insert(key, map.next_value::<UniqueValue>()?.0);
                }
                Ok(UniqueValue(values.into()))
            }
        }
        de.deserialize_any(Visitor)
    }
}

pub(super) fn query(v: &Value, include: &str, exclude: &str) -> Option<SearchQuery> {
    let tags = |key: &str| -> Option<Vec<String>> {
        match v.get(key) {
            None => Some(Vec::new()),
            Some(value) => {
                let items = value.as_array()?;
                if items.len() > 128 {
                    return None;
                }
                items
                    .iter()
                    .map(|item| {
                        let s = item.as_str()?;
                        (s.len() <= 256).then(|| s.to_owned())
                    })
                    .collect()
            }
        }
    };
    let rating = match v.get("RatingFilterIndex") {
        None => 0, // C# int default; no silent coercion of supplied invalid values.
        Some(value) => value.as_u64()?,
    };
    let ratings = match rating {
        0 => RatingFilter::Any,
        1 => RatingFilter::Only(vec![Rating::Explicit]),
        2 => RatingFilter::Only(vec![Rating::Questionable]),
        3 => RatingFilter::Only(vec![Rating::Safe]),
        _ => return None,
    };
    let query = SearchQuery {
        include: tags(include)?,
        exclude: tags(exclude)?,
        ratings,
        ..Default::default()
    };
    query.e621_tags().ok()?;
    Some(query)
}

pub(super) fn settings(
    bytes: &[u8],
    warnings: &mut Vec<String>,
) -> Result<(Vec<SavedSearch>, SettingsPreview), String> {
    let settings = parse(bytes)?;
    let object = settings
        .as_object()
        .ok_or("legacy settings must be a JSON object")?;
    let raw = match object.get("SavedSearches") {
        None => "[]",
        Some(value) => value
            .as_str()
            .ok_or("legacy SavedSearches must be a JSON string")?,
    };
    let entries = parse(raw.as_bytes())?;
    let values = entries
        .as_array()
        .ok_or("legacy SavedSearches must contain an array")?;
    if values.len() > MAX_RECORDS {
        return Err("legacy saved search record limit".into());
    }
    let mut searches = Vec::new();
    for (i, v) in values.iter().enumerate() {
        let Some((name, query)) = v.as_object().and_then(|_| {
            let name = v.get("Name")?.as_str()?;
            if !valid_text(name) {
                return None;
            }
            Some((name, query(v, "IncludeTags", "ExcludeTags")?))
        }) else {
            warnings.push(format!(
                "Saved search {} rejected: invalid or unrepresentable fields",
                i + 1
            ));
            continue;
        };
        // Item identity is independent of credentials, array position and source path.
        let id = format!(
            "legacy-v1-{}",
            digest(&serde_json::to_vec(&(name, &query)).map_err(|_| "cannot encode search")?)
        );
        if searches.iter().any(|s: &SavedSearch| s.id == id) {
            warnings.push(format!(
                "Saved search {} rejected: duplicate content",
                i + 1
            ));
            continue;
        }
        searches.push(SavedSearch {
            id,
            name: name.into(),
            query,
        });
    }
    let mut preview = SettingsPreview::default();
    for (key, value) in object {
        let accepted = match key.as_str() {
            "FilenameTemplate" | "PoolFilenameTemplate" => {
                let parsed = value
                    .as_str()
                    .filter(|s| s.len() <= MAX_TEXT_BYTES && FilenameTemplate::parse(s).is_ok())
                    .map(str::to_owned);
                let ok = parsed.is_some();
                if key == "FilenameTemplate" {
                    preview.filename_template = parsed;
                } else {
                    preview.pool_filename_template = parsed;
                }
                ok
            }
            "ConcurrentDownloads" => {
                preview.concurrent_downloads = value
                    .as_u64()
                    .filter(|v| (1..=4).contains(v))
                    .map(|v| v as u8);
                preview.concurrent_downloads.is_some()
            }
            "GalleryScale" => {
                preview.gallery_scale = value
                    .as_f64()
                    .filter(|v| v.is_finite() && (0.5..=2.0).contains(v));
                preview.gallery_scale.is_some()
            }
            "SaveMetadataJson" => {
                preview.save_metadata = value.as_bool();
                preview.save_metadata.is_some()
            }
            "LoadLastSessionEnabled" => {
                preview.requested_session_restore = value.as_bool();
                preview.requested_session_restore.is_some()
            }
            "DefaultDownloadDirectory" => {
                preview.directory_hint =
                    value.as_str().filter(|s| valid_text(s)).map(str::to_owned);
                preview.directory_hint.is_some()
            }
            "DownloadDuplicatesPolicy" => {
                preview.duplicates = match value.as_str() {
                    Some("skip") => Some(DuplicatePolicy::Skip),
                    Some("overwrite") => Some(DuplicatePolicy::Overwrite),
                    Some("rename") => Some(DuplicatePolicy::Rename),
                    _ => None,
                };
                preview.duplicates.is_some()
            }
            _ => continue, // Unknown fields (including all credential fields) never leave this function.
        };
        if !accepted {
            warnings.push(format!(
                "Legacy setting {key} rejected: invalid or incompatible value"
            ));
        }
    }
    Ok((searches, preview))
}
