//! Non-authoritative, bounded submitted browsing intent. Never credentials or media grants.
use furchive_domain::{RatingFilter, SearchQuery, SortOrder};
use serde::{Deserialize, Serialize};

/// Closed persisted DTO: domain SearchQuery is intentionally permissive elsewhere.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SessionQuery {
    pub include: Vec<String>,
    pub exclude: Vec<String>,
    pub ratings: RatingFilter,
    pub artist: Option<String>,
    pub sort: SortOrder,
}
impl From<SearchQuery> for SessionQuery {
    fn from(q: SearchQuery) -> Self {
        Self {
            include: q.include,
            exclude: q.exclude,
            ratings: q.ratings,
            artist: q.artist,
            sort: q.sort,
        }
    }
}
impl From<SessionQuery> for SearchQuery {
    fn from(q: SessionQuery) -> Self {
        Self {
            include: q.include,
            exclude: q.exclude,
            ratings: q.ratings,
            artist: q.artist,
            sort: q.sort,
        }
    }
}
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "mode", rename_all = "snake_case", deny_unknown_fields)]
pub enum BrowsingSession {
    Search {
        query: SessionQuery,
        page: u32,
    },
    Favorites {
        query: SessionQuery,
        username: String,
        page: u32,
    },
    Pool {
        id: u64,
        ratings: RatingFilter,
        offset: usize,
    },
}
impl BrowsingSession {
    pub fn validate(&self) -> Result<(), String> {
        fn query(q: &SessionQuery, page: u32) -> Result<SearchQuery, String> {
            if !(1..=100_000).contains(&page)
                || q.include.len() + q.exclude.len() > 100
                || q.include.iter().chain(&q.exclude).any(|t| t.len() > 256)
                || q.artist.as_ref().is_some_and(|s| s.len() > 256)
            {
                return Err("remembered browsing intent exceeds limits".into());
            }
            let q: SearchQuery = q.clone().into();
            q.e621_tags().map_err(|_| "invalid remembered query")?;
            Ok(q)
        }
        match self {
            Self::Search { query: q, page } => {
                query(q, *page)?;
            }
            Self::Favorites {
                query: q,
                username,
                page,
            } => {
                crate::browse::favorites_query(&query(q, *page)?, username)?;
            }
            Self::Pool {
                id,
                ratings,
                offset,
            } => {
                if *id == 0
                    || *id > 9_007_199_254_740_991
                    || *offset > 10_000
                    || !offset.is_multiple_of(50)
                {
                    return Err("invalid remembered pool position".into());
                }
                SearchQuery {
                    ratings: ratings.clone(),
                    ..Default::default()
                }
                .e621_tags()
                .map_err(|_| "invalid remembered pool ratings")?;
            }
        }
        if serde_json::to_vec(self)
            .map_err(|_| "invalid remembered intent")?
            .len()
            > 32 * 1024
        {
            return Err("remembered browsing intent exceeds 32KiB".into());
        }
        Ok(())
    }
    pub fn from_cursor(intent: &crate::viewer_cursor::Intent, page: u32, offset: usize) -> Self {
        use crate::viewer_cursor::Intent;
        match intent {
            Intent::Search(q) => Self::Search {
                query: q.clone().into(),
                page,
            },
            Intent::Favorites { query, username } => Self::Favorites {
                query: query.clone().into(),
                username: username.clone(),
                page,
            },
            Intent::Pool { id, ratings } => Self::Pool {
                id: *id,
                ratings: ratings.clone(),
                offset,
            },
        }
    }
}
/// In-memory fence, not a persistent or renderer-supplied capability.
#[derive(Clone, Copy)]
pub struct SessionTicket {
    pub(crate) revision: u64,
    pub(crate) request: u64,
}
