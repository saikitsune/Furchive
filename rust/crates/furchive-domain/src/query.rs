use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Unknown/restricted upstream ratings must never silently become Safe.
#[derive(Debug, Clone, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Rating {
    Safe,
    Questionable,
    Explicit,
    Restricted,
    Unknown(String),
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RatingFilter {
    Any,
    Only(Vec<Rating>),
}

impl Default for RatingFilter {
    fn default() -> Self {
        Self::Only(vec![Rating::Safe])
    }
}

impl RatingFilter {
    pub fn accepts(&self, rating: &Rating) -> bool {
        matches!(
            rating,
            Rating::Safe | Rating::Questionable | Rating::Explicit
        ) && match self {
            Self::Any => true,
            Self::Only(values) => values.contains(rating),
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SortOrder {
    #[default]
    Newest,
    Oldest,
    Score,
    Favorites,
    Random,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct SearchQuery {
    pub include: Vec<String>,
    pub exclude: Vec<String>,
    pub ratings: RatingFilter,
    pub artist: Option<String>,
    pub sort: SortOrder,
}

#[derive(Debug, Error, PartialEq, Eq)]
#[error("invalid query: {0}")]
pub struct QueryError(pub String);

fn validate_token(tag: &str) -> Result<(), QueryError> {
    if tag.is_empty()
        || tag.chars().any(|c| c.is_whitespace() || c.is_control())
        || tag.starts_with(['-', '~'])
    {
        return Err(QueryError(
            "chips must contain one unsigned nonempty token".into(),
        ));
    }
    Ok(())
}

impl SearchQuery {
    /// Raw e621 tags, not URL-encoded. Multi-rating selections require accepts()
    /// on returned posts; pagination must still use the upstream raw count.
    pub fn e621_tags(&self) -> Result<String, QueryError> {
        let mut terms = Vec::new();
        for (tags, prefix) in [(&self.include, ""), (&self.exclude, "-")] {
            for tag in tags {
                validate_token(tag)?;
                let tag = if tag.eq_ignore_ascii_case("no_artist") {
                    "arttags:0"
                } else {
                    tag
                };
                terms.push(format!("{prefix}{tag}"));
            }
        }
        if let RatingFilter::Only(ratings) = &self.ratings {
            let mut ratings = ratings.clone();
            ratings.sort();
            ratings.dedup();
            if ratings.is_empty() || ratings.iter().any(|r| !RatingFilter::Any.accepts(r)) {
                return Err(QueryError(
                    "rating selection must contain known searchable ratings".into(),
                ));
            }
            if ratings.len() == 1 {
                terms.push(
                    match ratings[0] {
                        Rating::Safe => "rating:s",
                        Rating::Questionable => "rating:q",
                        _ => "rating:e",
                    }
                    .into(),
                );
            }
        }
        if let Some(artist) = &self.artist {
            validate_token(artist)?;
            terms.push(format!("artist:{artist}"));
        }
        terms.push(format!(
            "order:{}",
            match self.sort {
                SortOrder::Newest => "id_desc",
                SortOrder::Oldest => "id_asc",
                SortOrder::Score => "score",
                SortOrder::Favorites => "favcount",
                SortOrder::Random => "random",
            }
        ));
        Ok(terms.join(" "))
    }
}
