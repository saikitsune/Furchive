//! Rust-owned submitted-query edge navigation. Renderer supplies only selected ID
//! and a signed single step, never a replacement query, URL or page authority.
use furchive_domain::{RatingFilter, SearchQuery};
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Intent {
    Search(SearchQuery),
    Favorites {
        query: SearchQuery,
        username: String,
    },
    Pool {
        id: u64,
        ratings: RatingFilter,
    },
}
#[derive(Clone, Debug)]
pub struct Turn {
    pub intent: Intent,
    pub page: u32,
    pub offset: usize,
    pub previous: bool,
}
struct Page {
    generation: u64,
    intent: Intent,
    page: u32,
    offset: usize,
    total: usize,
    next: Option<usize>,
    ids: Vec<String>,
}
#[derive(Default)]
pub struct ViewerCursor(std::sync::Mutex<Option<Page>>);
impl ViewerCursor {
    /// Only submitted intent/position; selected ID and grants are deliberately omitted.
    pub fn browsing_session(
        &self,
        generation: u64,
    ) -> Result<crate::browsing_session::BrowsingSession, String> {
        let current = self.0.lock().map_err(|_| "viewer cursor poisoned")?;
        let page = current
            .as_ref()
            .filter(|p| p.generation == generation)
            .ok_or("viewer cursor expired")?;
        let session = crate::browsing_session::BrowsingSession::from_cursor(
            &page.intent,
            page.page,
            page.offset,
        );
        session.validate()?;
        Ok(session)
    }
    pub fn clear(&self) -> Result<(), String> {
        *self.0.lock().map_err(|_| "viewer cursor poisoned")? = None;
        Ok(())
    }
    pub fn search(
        &self,
        generation: u64,
        intent: Intent,
        page: u32,
        next: Option<u32>,
        ids: Vec<String>,
    ) -> Result<(), String> {
        if page == 0 || next.is_some_and(|n| n <= page) || matches!(intent, Intent::Pool { .. }) {
            return Err("invalid search cursor".into());
        }
        match &intent {
            Intent::Search(q) => {
                q.e621_tags().map_err(|e| e.to_string())?;
            }
            Intent::Favorites { query, username } => {
                crate::browse::favorites_query(query, username)?;
            }
            _ => unreachable!(),
        }
        self.replace(Page {
            generation,
            intent,
            page,
            offset: 0,
            total: 0,
            next: next.map(|n| n as usize),
            ids,
        })
    }
    #[allow(clippy::too_many_arguments)]
    pub fn pool(
        &self,
        generation: u64,
        id: u64,
        ratings: RatingFilter,
        offset: usize,
        total: usize,
        next: Option<usize>,
        ids: Vec<String>,
    ) -> Result<(), String> {
        SearchQuery {
            ratings: ratings.clone(),
            ..Default::default()
        }
        .e621_tags()
        .map_err(|e| e.to_string())?;
        if id == 0
            || offset > total
            || !offset.is_multiple_of(50)
            || next.is_some_and(|n| n <= offset || n >= total || !n.is_multiple_of(50))
        {
            return Err("invalid pool cursor".into());
        }
        self.replace(Page {
            generation,
            intent: Intent::Pool { id, ratings },
            page: 1,
            offset,
            total,
            next,
            ids,
        })
    }
    fn replace(&self, page: Page) -> Result<(), String> {
        let mut seen = std::collections::HashSet::new();
        if page.ids.len() > 100
            || page.ids.iter().any(|id| {
                id.parse::<u64>()
                    .ok()
                    .filter(|n| *n > 0)
                    .map(|n| n.to_string())
                    .as_deref()
                    != Some(id)
                    || !seen.insert(id)
            })
        {
            return Err("invalid cursor post IDs".into());
        }
        let mut current = self.0.lock().map_err(|_| "viewer cursor poisoned")?;
        if current
            .as_ref()
            .is_some_and(|old| old.generation > page.generation)
        {
            return Err("superseded viewer cursor".into());
        }
        *current = Some(page);
        Ok(())
    }
    pub fn turn(&self, generation: u64, id: &str, delta: i8) -> Result<Option<Turn>, String> {
        let state = self.0.lock().map_err(|_| "viewer cursor poisoned")?;
        let p = state
            .as_ref()
            .filter(|p| p.generation == generation)
            .ok_or("viewer cursor expired")?;
        let edge = match delta {
            -1 => p.ids.first(),
            1 => p.ids.last(),
            _ => return Err("viewer step must be -1 or 1".into()),
        };
        if edge.map(String::as_str) != Some(id) {
            return Err("viewer step requires current edge selection".into());
        }
        let previous = delta == -1;
        let (mut page, mut offset) = (p.page, p.offset);
        match p.intent {
            Intent::Pool { .. } => {
                if previous {
                    if offset == 0 {
                        return Ok(None);
                    }
                    offset = offset.saturating_sub(50);
                } else {
                    let Some(n) = p.next else {
                        return Ok(None);
                    };
                    if n >= p.total {
                        return Ok(None);
                    }
                    offset = n;
                }
            }
            _ => {
                if previous {
                    if page == 1 {
                        return Ok(None);
                    }
                    page -= 1;
                } else {
                    let Some(n) = p.next else {
                        return Ok(None);
                    };
                    page = u32::try_from(n).map_err(|_| "page overflow")?;
                }
            }
        }
        Ok(Some(Turn {
            intent: p.intent.clone(),
            page,
            offset,
            previous,
        }))
    }
}
#[derive(serde::Serialize)]
pub struct NavigationPage {
    pub selected_id: String,
    pub viewable_ids: Vec<String>,
    pub page: u32,
    pub offset: usize,
    pub search: Option<furchive_e621::SearchPage>,
    pub pool: Option<crate::browse::PoolPage>,
}
impl ViewerCursor {
    pub async fn navigate<P: crate::browse::Provider>(
        &self,
        browser: &crate::browse::Browser<P>,
        cache: &crate::media_cache::MediaCache,
        id: &str,
        delta: i8,
    ) -> Result<Option<NavigationPage>, String> {
        let generation = cache.generation()?;
        let Some(mut turn) = self.turn(generation, id, delta)? else {
            return Ok(None);
        };
        // At most four metadata pages per deliberate edge intent. No unbounded scans.
        for _ in 0..4 {
            let (search, pool) = match &turn.intent {
                Intent::Search(q) => (Some(browser.search(q, turn.page).await?), None),
                Intent::Favorites { query, username } => (
                    Some(browser.favorites(query, username, turn.page).await?),
                    None,
                ),
                Intent::Pool { id, ratings } => (
                    None,
                    Some(browser.pool_page(*id, turn.offset, 50, ratings).await?),
                ),
            };
            if cache.generation()? != generation {
                return Err("viewer navigation superseded".into());
            }
            let posts = if let Some(p) = &search {
                p.items.clone()
            } else {
                pool.as_ref()
                    .unwrap()
                    .entries
                    .iter()
                    .filter_map(|e| e.post.clone())
                    .collect()
            };
            let selectable = posts
                .iter()
                .filter(|p| {
                    p.full_url
                        .as_deref()
                        .is_some_and(|u| crate::media_cache::canonical_media(u).is_ok())
                })
                .map(|p| p.key.id().to_owned())
                .collect::<Vec<_>>();
            if !selectable.is_empty() {
                let selected_id = if turn.previous {
                    selectable.last()
                } else {
                    selectable.first()
                }
                .unwrap()
                .clone();
                let nextgen = cache.begin_page_if(generation)?;
                cache.authorize_page(nextgen, &posts)?;
                if let Some(p) = &search {
                    self.search(
                        nextgen,
                        turn.intent.clone(),
                        turn.page,
                        p.next_page,
                        selectable.clone(),
                    )?;
                } else {
                    let p = pool.as_ref().unwrap();
                    let Intent::Pool { id, ratings } = &turn.intent else {
                        unreachable!()
                    };
                    self.pool(
                        nextgen,
                        *id,
                        ratings.clone(),
                        turn.offset,
                        p.total,
                        p.next_offset,
                        selectable.clone(),
                    )?;
                }
                return Ok(Some(NavigationPage {
                    selected_id,
                    viewable_ids: selectable,
                    page: turn.page,
                    offset: turn.offset,
                    search,
                    pool,
                }));
            }
            let nextpage = search.as_ref().and_then(|p| p.next_page);
            let nextoffset = pool.as_ref().and_then(|p| p.next_offset);
            let Some(next) = turn.follow_empty(nextpage, nextoffset)? else {
                return Ok(None);
            };
            turn = next;
        }
        Err("No viewable media within four pages; use page controls to continue".into())
    }
}
impl Turn {
    /// Skip at most the host's finite batch budget of entirely hidden pages; caller
    /// never loops without a cap. Bad/non-progressing provider cursors fail closed.
    pub fn follow_empty(
        mut self,
        next_page: Option<u32>,
        next_offset: Option<usize>,
    ) -> Result<Option<Self>, String> {
        match self.intent {
            Intent::Pool { .. } => {
                if self.previous {
                    if self.offset == 0 {
                        return Ok(None);
                    }
                    self.offset = self.offset.saturating_sub(50);
                } else {
                    let Some(n) = next_offset else {
                        return Ok(None);
                    };
                    if n <= self.offset || !n.is_multiple_of(50) {
                        return Err("pool cursor did not advance".into());
                    }
                    self.offset = n;
                }
            }
            _ => {
                if self.previous {
                    if self.page == 1 {
                        return Ok(None);
                    }
                    self.page -= 1;
                } else {
                    let Some(n) = next_page else {
                        return Ok(None);
                    };
                    if n <= self.page {
                        return Err("search cursor did not advance".into());
                    }
                    self.page = n;
                }
            }
        }
        Ok(Some(self))
    }
}
