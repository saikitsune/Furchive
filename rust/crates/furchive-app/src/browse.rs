//! UI-independent browsing projections. Account-hidden records remain holes, not
//! authority to bypass filters or to reuse another account's cached media.
use furchive_domain::{MediaItem, Pool, RatingFilter, SearchQuery};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::future::Future;
/// Narrow async boundary permits deterministic harmless metadata integration tests.
pub trait Provider: Send + Sync {
    fn search(
        &self,
        query: &SearchQuery,
        page: u32,
        limit: usize,
    ) -> impl Future<Output = Result<furchive_e621::SearchPage, String>> + Send;
    fn pool(&self, id: u64) -> impl Future<Output = Result<Pool, String>> + Send;
    fn posts(&self, ids: &[String]) -> impl Future<Output = Result<Vec<MediaItem>, String>> + Send;
    fn suggestions(
        &self,
        prefix: &str,
    ) -> impl Future<Output = Result<Vec<furchive_e621::TagSuggestion>, String>> + Send;
}
impl Provider for furchive_e621::E621Client {
    async fn search(
        &self,
        q: &SearchQuery,
        page: u32,
        limit: usize,
    ) -> Result<furchive_e621::SearchPage, String> {
        self.search(q, page, limit).await
    }
    async fn pool(&self, id: u64) -> Result<Pool, String> {
        self.pool(id).await
    }
    async fn posts(&self, ids: &[String]) -> Result<Vec<MediaItem>, String> {
        self.posts_by_ids(ids).await
    }
    async fn suggestions(&self, prefix: &str) -> Result<Vec<furchive_e621::TagSuggestion>, String> {
        self.suggestions(prefix, 10).await
    }
}
pub struct Browser<P> {
    provider: P,
    db: crate::Database,
}
impl<P: Provider> Browser<P> {
    pub fn new(provider: P, db: crate::Database) -> Self {
        Self { provider, db }
    }
    pub async fn search(
        &self,
        query: &SearchQuery,
        page: u32,
    ) -> Result<furchive_e621::SearchPage, String> {
        query.e621_tags().map_err(|e| e.to_string())?;
        if page == 0 {
            return Err("page must be positive".into());
        }
        let result = self.provider.search(query, page, 50).await?;
        self.db.store_posts(result.items.clone()).await?;
        Ok(result)
    }
    pub async fn favorites(
        &self,
        query: &SearchQuery,
        username: &str,
        page: u32,
    ) -> Result<furchive_e621::SearchPage, String> {
        self.search(&favorites_query(query, username)?, page).await
    }
    pub async fn suggestions(
        &self,
        prefix: &str,
    ) -> Result<Vec<furchive_e621::TagSuggestion>, String> {
        self.provider.suggestions(prefix).await
    }
    pub async fn pool_page(
        &self,
        id: u64,
        offset: usize,
        limit: usize,
        ratings: &RatingFilter,
    ) -> Result<PoolPage, String> {
        if id == 0 {
            return Err("invalid pool ID".into());
        }
        SearchQuery {
            ratings: ratings.clone(),
            ..Default::default()
        }
        .e621_tags()
        .map_err(|e| e.to_string())?;
        let pool = self.provider.pool(id).await?;
        if pool.key.id() != id.to_string() {
            return Err("pool response identity mismatch".into());
        }
        let ids = pool_slice(&pool, offset, limit)?;
        let posts = if ids.is_empty() {
            Vec::new()
        } else {
            self.provider.posts(ids).await?
        };
        let page = assemble_pool_page(pool, posts, offset, limit, ratings)?;
        self.db.store_pool(page.pool.clone()).await?;
        self.db
            .store_posts(page.entries.iter().filter_map(|e| e.post.clone()).collect())
            .await?;
        Ok(page)
    }
    pub async fn pins(&self) -> Result<Vec<furchive_domain::PoolKey>, String> {
        self.db.pins().await
    }
    pub async fn set_pin(&self, id: u64, pinned: bool) -> Result<(), String> {
        if id == 0 {
            return Err("invalid pool ID".into());
        }
        self.db
            .set_pin(
                furchive_domain::PoolKey::new("e621", id.to_string()).map_err(|e| e.to_string())?,
                pinned,
            )
            .await
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PoolEntry {
    pub id: String,
    pub position: usize,
    pub post: Option<MediaItem>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PoolPage {
    pub pool: Pool,
    pub entries: Vec<PoolEntry>,
    pub total: usize,
    pub next_offset: Option<usize>,
}
/// Explicit whole-pool metadata intent. Snapshot once; never fetch missing IDs through
/// an unfiltered endpoint. No queue mutation until every batch is validated.
pub async fn collect_pool_download<P: Provider>(
    provider: &P,
    id: u64,
    ratings: &RatingFilter,
) -> Result<PoolPage, String> {
    if id == 0 {
        return Err("invalid pool ID".into());
    }
    SearchQuery {
        ratings: ratings.clone(),
        ..Default::default()
    }
    .e621_tags()
    .map_err(|e| e.to_string())?;
    let pool = provider.pool(id).await?;
    if pool.key.id() != id.to_string() {
        return Err("pool response identity mismatch".into());
    }
    if pool.post_ids.len() > 10_000 {
        return Err("whole-pool download limited to 10000 members".into());
    }
    pool_slice(&pool, 0, 1)?;
    let mut entries = Vec::with_capacity(pool.post_ids.len());
    for offset in (0..pool.post_ids.len()).step_by(100) {
        let ids = pool_slice(&pool, offset, 100)?;
        let posts = provider.posts(ids).await?;
        let page = assemble_pool_page(pool.clone(), posts, offset, 100, ratings)?;
        entries.extend(page.entries);
    }
    let total = pool.post_ids.len();
    Ok(PoolPage {
        pool,
        entries,
        total,
        next_offset: None,
    })
}

pub fn favorites_query(query: &SearchQuery, username: &str) -> Result<SearchQuery, String> {
    if username.is_empty()
        || username.len() > 100
        || !username
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'_')
    {
        return Err("username must contain only letters, digits or underscores".into());
    }
    let mut result = query.clone();
    let token = format!("fav:{username}");
    if !result.include.contains(&token) {
        result.include.push(token);
    }
    result.e621_tags().map_err(|e| e.to_string())?;
    Ok(result)
}
pub fn pool_slice(pool: &Pool, offset: usize, limit: usize) -> Result<&[String], String> {
    if pool.key.source() != "e621"
        || pool
            .key
            .id()
            .parse::<u64>()
            .ok()
            .filter(|id| *id > 0)
            .map(|id| id.to_string())
            .as_deref()
            != Some(pool.key.id())
    {
        return Err("invalid e621 pool identity".into());
    }
    let mut ids = HashSet::new();
    for id in &pool.post_ids {
        if id
            .parse::<u64>()
            .ok()
            .filter(|id| *id > 0)
            .map(|id| id.to_string())
            .as_deref()
            != Some(id.as_str())
            || !ids.insert(id)
        {
            return Err("invalid or duplicate pool membership".into());
        }
    }
    if !(1..=100).contains(&limit) || offset > pool.post_ids.len() {
        return Err("invalid pool page".into());
    }
    Ok(&pool.post_ids[offset..offset.saturating_add(limit).min(pool.post_ids.len())])
}
pub fn assemble_pool_page(
    pool: Pool,
    posts: Vec<MediaItem>,
    offset: usize,
    limit: usize,
    ratings: &RatingFilter,
) -> Result<PoolPage, String> {
    let ids = pool_slice(&pool, offset, limit)?;
    // Validate the same rating contract as normal searches, including empty selections.
    SearchQuery {
        ratings: ratings.clone(),
        ..Default::default()
    }
    .e621_tags()
    .map_err(|e| e.to_string())?;
    let mut mapped = HashMap::new();
    for post in posts {
        if post.key.source() != "e621"
            || !ids.contains(&post.key.id().to_owned())
            || mapped.contains_key(post.key.id())
        {
            return Err("unrequested or duplicate pool page post".into());
        }
        mapped.insert(post.key.id().to_owned(), post);
    }
    let entries = ids
        .iter()
        .enumerate()
        .map(|(index, id)| PoolEntry {
            id: id.clone(),
            position: offset + index,
            post: mapped.remove(id).filter(|p| ratings.accepts(&p.rating)),
        })
        .collect();
    let total = pool.post_ids.len();
    let end = offset.saturating_add(limit).min(total);
    Ok(PoolPage {
        pool,
        entries,
        total,
        next_offset: (end < total).then_some(end),
    })
}
