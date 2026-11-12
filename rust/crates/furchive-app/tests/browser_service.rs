//! Service-level HTTP substitute is explicitly local metadata, not e621 integration proof.
use furchive_app::{
    Database,
    browse::{Browser, Provider},
};
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, Rating, RatingFilter, SearchQuery};
use furchive_e621::{SearchPage, TagSuggestion};
use std::sync::{Arc, Mutex};
#[derive(Clone, Default)]
struct Fixture {
    calls: Arc<Mutex<Vec<String>>>,
}
impl Provider for Fixture {
    async fn search(&self, q: &SearchQuery, page: u32, limit: usize) -> Result<SearchPage, String> {
        self.calls.lock().unwrap().push(q.e621_tags().unwrap());
        assert_eq!(limit, 50);
        Ok(SearchPage {
            items: vec![],
            next_page: (page == 1).then_some(2),
            raw_count: 50,
        })
    }
    async fn pool(&self, id: u64) -> Result<Pool, String> {
        let mut p = Pool::new(
            PoolKey::new("e621", id.to_string()).unwrap(),
            "Local test pool".into(),
        );
        p.post_ids = vec!["10".into(), "2".into(), "30".into()];
        Ok(p)
    }
    async fn posts(&self, ids: &[String]) -> Result<Vec<MediaItem>, String> {
        self.calls.lock().unwrap().push(ids.join(","));
        let mut p = MediaItem::new(PostKey::new("e621", "2").unwrap());
        p.rating = Rating::Safe;
        Ok(vec![p])
    }
    async fn suggestions(&self, _: &str) -> Result<Vec<TagSuggestion>, String> {
        Ok(vec![])
    }
}
#[tokio::test]
async fn browser_joins_ordered_api_pool_and_persistent_pin_state_without_reviving_cache_holes() {
    let dir = tempfile::tempdir().unwrap();
    let db = Database::open(dir.path()).await.unwrap();
    let fixture = Fixture::default();
    let browser = Browser::new(fixture.clone(), db.clone());
    let page = browser
        .pool_page(3, 0, 3, &RatingFilter::Any)
        .await
        .unwrap();
    assert_eq!(
        page.entries
            .iter()
            .map(|e| e.post.as_ref().map(|p| p.key.id()))
            .collect::<Vec<_>>(),
        vec![None, Some("2"), None]
    );
    assert_eq!(fixture.calls.lock().unwrap().as_slice(), ["10,2,30"]);
    browser.set_pin(3, true).await.unwrap();
    assert_eq!(browser.pins().await.unwrap()[0].id(), "3");
    assert!(browser.set_pin(0, true).await.is_err());
    db.shutdown().await.unwrap();
    let db = Database::open(dir.path()).await.unwrap();
    assert_eq!(db.pins().await.unwrap()[0].id(), "3");
    db.shutdown().await.unwrap();
}
#[tokio::test]
async fn browser_favorites_preserves_include_exclude_ratings_and_raw_pagination() {
    let dir = tempfile::tempdir().unwrap();
    let db = Database::open(dir.path()).await.unwrap();
    let fixture = Fixture::default();
    let browser = Browser::new(fixture.clone(), db.clone());
    let query = SearchQuery {
        include: vec!["fox".into()],
        exclude: vec!["comic".into()],
        ..Default::default()
    };
    let page = browser.favorites(&query, "fixture_user", 1).await.unwrap();
    assert_eq!(page.next_page, Some(2));
    assert_eq!(page.raw_count, 50);
    assert_eq!(
        fixture.calls.lock().unwrap()[0],
        "fox fav:fixture_user -comic rating:s order:id_desc"
    );
    assert!(browser.favorites(&query, "user rating:e", 1).await.is_err());
    assert_eq!(fixture.calls.lock().unwrap().len(), 1);
    db.shutdown().await.unwrap();
}
