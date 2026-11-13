use furchive_app::{
    Database,
    browse::{Browser, Provider},
    media_cache::MediaCache,
    viewer_cursor::{Intent, ViewerCursor},
};
use furchive_domain::{MediaItem, Pool, PostKey, SearchQuery};
use std::sync::{Arc, Mutex};
struct Fixtures(Arc<Mutex<Vec<u32>>>);
impl Provider for Fixtures {
    async fn search(
        &self,
        q: &SearchQuery,
        page: u32,
        _: usize,
    ) -> Result<furchive_e621::SearchPage, String> {
        assert_eq!(q.include, vec!["fox"]);
        self.0.lock().unwrap().push(page);
        Ok(furchive_e621::SearchPage {
            items: if page == 2 {
                vec![]
            } else {
                vec![post(if page == 1 { "100" } else { "300" })]
            },
            next_page: (page < 3).then_some(page + 1),
            raw_count: 50,
        })
    }
    async fn pool(&self, _: u64) -> Result<Pool, String> {
        Err("unexpected pool".into())
    }
    async fn posts(&self, _: &[String]) -> Result<Vec<MediaItem>, String> {
        Err("unexpected posts".into())
    }
    async fn suggestions(&self, _: &str) -> Result<Vec<furchive_e621::TagSuggestion>, String> {
        Ok(vec![])
    }
}
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p
}
#[tokio::test]
async fn viewer_step_skips_empty_filtered_pages_and_commits_only_submitted_current_authority() {
    let t = tempfile::tempdir().unwrap();
    let db = Database::open(t.path()).await.unwrap();
    let calls = Arc::new(Mutex::new(vec![]));
    let browser = Browser::new(Fixtures(calls.clone()), db.clone());
    let cache = MediaCache::new(1024).unwrap();
    cache.authorize(&[post("100")]).unwrap();
    let q = SearchQuery {
        include: vec!["fox".into()],
        ..Default::default()
    };
    let cursor = ViewerCursor::default();
    cursor
        .search(
            cache.generation().unwrap(),
            Intent::Search(q),
            1,
            Some(2),
            vec!["100".into()],
        )
        .unwrap();
    let result = cursor
        .navigate(&browser, &cache, "100", 1)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(result.selected_id, "300");
    assert_eq!(result.page, 3);
    assert_eq!(calls.lock().unwrap().as_slice(), [2, 3]);
    assert!(cache.authorized_post("100").is_err());
    assert_eq!(cache.authorized_post("300").unwrap().key.id(), "300");
    assert!(
        cursor
            .navigate(&browser, &cache, "foreign", 1)
            .await
            .is_err()
    );
    let back = cursor
        .navigate(&browser, &cache, "300", -1)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(back.selected_id, "100");
    assert_eq!(back.page, 1);
    cache.begin_page().unwrap();
    assert!(cursor.navigate(&browser, &cache, "100", 1).await.is_err());
    db.shutdown().await.unwrap();
}
