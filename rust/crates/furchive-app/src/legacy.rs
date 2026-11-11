//! Read-only legacy settings preview; secret fields are deliberately never retained.
use furchive_domain::{Rating, RatingFilter, SearchQuery};
use furchive_engine::storage::SavedSearch;
use std::path::Path;
#[derive(Debug)]
pub struct LegacyPreview {
    pub searches: Vec<SavedSearch>,
    pub warnings: Vec<String>,
}
pub fn read_settings(root: &Path) -> Result<LegacyPreview, String> {
    let path = root.join("settings.json");
    let file = std::fs::File::open(path).map_err(|_| "cannot open legacy settings")?;
    if file
        .metadata()
        .map_err(|_| "cannot inspect legacy settings")?
        .len()
        > 1024 * 1024
    {
        return Err("legacy settings exceeds 1MiB".into());
    }
    let settings: serde_json::Value =
        serde_json::from_reader(file).map_err(|_| "legacy settings JSON invalid")?;
    let object = settings
        .as_object()
        .ok_or("legacy settings must be a JSON object")?;
    let raw = match object.get("SavedSearches") {
        None => "[]",
        Some(value) => value
            .as_str()
            .ok_or("legacy SavedSearches must be a JSON string")?,
    };
    let values: Vec<serde_json::Value> =
        serde_json::from_str(raw).map_err(|_| "legacy saved-search JSON invalid")?;
    let mut searches = Vec::new();
    let mut warnings = Vec::new();
    for (i, v) in values.into_iter().enumerate() {
        let name = v["Name"].as_str().unwrap_or_default();
        let strings = |key: &str| -> Result<Vec<String>, ()> {
            match v.get(key) {
                None => Ok(Vec::new()),
                Some(value) => value
                    .as_array()
                    .ok_or(())?
                    .iter()
                    .map(|x| x.as_str().map(str::to_owned).ok_or(()))
                    .collect(),
            }
        };
        let (Ok(include), Ok(exclude)) = (strings("IncludeTags"), strings("ExcludeTags")) else {
            warnings.push(format!(
                "Saved search {} rejected: invalid chip types",
                i + 1
            ));
            continue;
        };
        let ratings = match v["RatingFilterIndex"].as_u64().unwrap_or(3) {
            0 => RatingFilter::Any,
            1 => RatingFilter::Only(vec![Rating::Explicit]),
            2 => RatingFilter::Only(vec![Rating::Questionable]),
            _ => RatingFilter::Only(vec![Rating::Safe]),
        };
        let query = SearchQuery {
            include,
            exclude,
            ratings,
            ..Default::default()
        };
        if name.is_empty() || query.e621_tags().is_err() {
            warnings.push(format!("Saved search {} rejected", i + 1));
            continue;
        }
        searches.push(SavedSearch {
            id: format!("legacy-{i}"),
            name: name.into(),
            query,
        });
    }
    Ok(LegacyPreview { searches, warnings })
}
