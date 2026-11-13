use furchive_app::{
    Database,
    browse::{Browser, Provider},
    media_cache::MediaCache,
    viewer_cursor::{Intent, ViewerCursor},
};
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, Rating, RatingFilter, SearchQuery};
use std::sync::{Arc, Mutex};
struct Fixture {
    calls: Arc<Mutex<Vec<String>>>,
    gate: Option<Arc<tokio::sync::Notify>>,
    started: Option<Arc<tokio::sync::Notify>>,
    empty: bool,
}
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.rating = Rating::Safe;
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p
}
impl Provider for Fixture {
    async fn search(
        &self,
        q: &SearchQuery,
        page: u32,
        _: usize,
    ) -> Result<furchive_e621::SearchPage, String> {
        self.calls.lock().unwrap().push(q.e621_tags().unwrap());
        if let Some(n) = &self.started {
            n.notify_one();
        }
        if let Some(n) = &self.gate {
            n.notified().await;
        }
        Ok(furchive_e621::SearchPage {
            items: if self.empty {
                vec![]
            } else {
                vec![post("200")]
            },
            raw_count: 50,
            next_page: Some(page + 1),
        })
    }
    async fn pool(&self, _: u64) -> Result<Pool, String> {
        Ok(Pool {
            key: PoolKey::new("e621", "3").unwrap(),
            name: "Pool".into(),
            description: None,
            post_ids: (1..=105).map(|n| n.to_string()).collect(),
            updated_at: None,
            active: true,
            category: None,
            created_at: None,
            fetched_at: None,
        })
    }
    async fn posts(&self, ids: &[String]) -> Result<Vec<MediaItem>, String> {
        self.calls.lock().unwrap().push(ids.join(","));
        Ok(if ids.contains(&"103".into()) {
            let mut hidden = post("102");
            hidden.rating = Rating::Explicit;
            vec![post("103"), hidden]
        } else if ids.contains(&"2".into()) {
            vec![post("2")]
        } else {
            vec![]
        })
    }
    async fn suggestions(&self, _: &str) -> Result<Vec<furchive_e621::TagSuggestion>, String> {
        Ok(vec![])
    }
}
#[tokio::test]
async fn navigation_refuses_superseded_results_before_cache_or_cursor_replacement() {
    let t = tempfile::tempdir().unwrap();
    let db = Database::open(t.path()).await.unwrap();
    let started = Arc::new(tokio::sync::Notify::new());
    let gate = Arc::new(tokio::sync::Notify::new());
    let b = Browser::new(
        Fixture {
            calls: Default::default(),
            started: Some(started.clone()),
            gate: Some(gate.clone()),
            empty: false,
        },
        db.clone(),
    );
    let c = MediaCache::new(1024).unwrap();
    c.authorize(&[post("100")]).unwrap();
    let cursor = ViewerCursor::default();
    cursor
        .search(
            c.generation().unwrap(),
            Intent::Search(Default::default()),
            1,
            Some(2),
            vec!["100".into()],
        )
        .unwrap();
    let nav = cursor.navigate(&b, &c, "100", 1);
    let change = async {
        started.notified().await;
        c.authorize(&[post("900")]).unwrap();
        gate.notify_one();
    };
    let (result, _) = tokio::join!(nav, change);
    assert!(result.err().unwrap().contains("superseded"));
    assert_eq!(c.authorized_post("900").unwrap().key.id(), "900");
    assert!(c.authorized_post("200").is_err());
    db.shutdown().await.unwrap();
}
#[tokio::test]
async fn favorites_empty_page_scan_is_capped_at_four_without_changing_authority() {
    let t = tempfile::tempdir().unwrap();
    let db = Database::open(t.path()).await.unwrap();
    let calls = Arc::new(Mutex::new(vec![]));
    let b = Browser::new(
        Fixture {
            calls: calls.clone(),
            started: None,
            gate: None,
            empty: true,
        },
        db.clone(),
    );
    let c = MediaCache::new(1024).unwrap();
    c.authorize(&[post("100")]).unwrap();
    let cursor = ViewerCursor::default();
    cursor
        .search(
            c.generation().unwrap(),
            Intent::Favorites {
                query: SearchQuery {
                    include: vec!["fox".into()],
                    ..Default::default()
                },
                username: "fixture_user".into(),
            },
            1,
            Some(2),
            vec!["100".into()],
        )
        .unwrap();
    assert!(
        cursor
            .navigate(&b, &c, "100", 1)
            .await
            .err()
            .unwrap()
            .contains("four pages")
    );
    assert_eq!(calls.lock().unwrap().len(), 4);
    assert!(
        calls
            .lock()
            .unwrap()
            .iter()
            .all(|q| q.contains("fav:fixture_user") && q.contains("rating:s"))
    );
    assert!(c.authorized_post("100").is_ok());
    db.shutdown().await.unwrap();
}
#[tokio::test]
async fn pool_navigation_skips_missing_batch_and_filtered_members_in_original_order() {
    let t = tempfile::tempdir().unwrap();
    let db = Database::open(t.path()).await.unwrap();
    let calls = Arc::new(Mutex::new(vec![]));
    let b = Browser::new(
        Fixture {
            calls: calls.clone(),
            started: None,
            gate: None,
            empty: false,
        },
        db.clone(),
    );
    let c = MediaCache::new(1024).unwrap();
    c.authorize(&[post("2")]).unwrap();
    let cursor = ViewerCursor::default();
    cursor
        .pool(
            c.generation().unwrap(),
            3,
            RatingFilter::default(),
            0,
            105,
            Some(50),
            vec!["2".into()],
        )
        .unwrap();
    let p = cursor.navigate(&b, &c, "2", 1).await.unwrap().unwrap();
    assert_eq!(p.selected_id, "103");
    assert_eq!(p.offset, 100);
    assert_eq!(
        p.pool
            .unwrap()
            .entries
            .iter()
            .find(|e| e.id == "103")
            .unwrap()
            .position,
        102
    );
    assert!(c.authorized_post("102").is_err());
    assert_eq!(calls.lock().unwrap().len(), 2);
    let p = cursor.navigate(&b, &c, "103", -1).await.unwrap().unwrap();
    assert_eq!(p.selected_id, "2");
    assert_eq!(p.offset, 0);
    db.shutdown().await.unwrap();
}
