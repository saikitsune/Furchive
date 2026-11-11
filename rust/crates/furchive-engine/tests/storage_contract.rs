use furchive_domain::{MediaItem, PostKey};
use furchive_domain::{Pool, PoolKey};
use furchive_engine::storage::CacheStore;

fn pool(source: &str, id: &str, posts: &[&str]) -> Pool {
    let mut value = Pool::new(PoolKey::new(source, id).unwrap(), format!("pool {id}"));
    value.post_ids = posts.iter().map(|id| (*id).into()).collect();
    value
}

#[test]
fn pools_reconcile_equal_length_reorders_removals_and_many_memberships() {
    let mut db = CacheStore::in_memory().unwrap();
    let first = pool("e621", "1", &["30", "2", "10"]);
    let second = pool("e621", "2", &["2", "30"]);
    let foreign = pool("other", "1", &["10", "30"]);
    let cached = MediaItem::new(PostKey::new("e621", "30").unwrap());
    db.upsert_posts(std::slice::from_ref(&cached)).unwrap();
    for p in [&first, &second, &foreign] {
        db.reconcile_pool(p).unwrap();
    }
    let entries = db.pool_entries(&first.key).unwrap();
    assert_eq!(
        entries
            .iter()
            .map(|e| (e.key.id(), e.position))
            .collect::<Vec<_>>(),
        [("30", 0), ("2", 1), ("10", 2)]
    );
    assert_eq!(entries[0].post, Some(cached.clone()));
    assert_eq!(entries[1].post, None);
    let mut reordered = pool("e621", "1", &["10", "30", "99"]);
    reordered.name = "renamed".into();
    db.reconcile_pool(&reordered).unwrap();
    assert_eq!(db.get_pool(&first.key).unwrap(), Some(reordered));
    assert_eq!(db.get_pool(&second.key).unwrap(), Some(second));
    assert_eq!(db.get_pool(&foreign.key).unwrap(), Some(foreign));
    let empty = pool("e621", "1", &[]);
    db.reconcile_pool(&empty).unwrap();
    assert!(db.pool_entries(&first.key).unwrap().is_empty());
    assert_eq!(db.get_post(&cached.key).unwrap(), Some(cached));
    assert!(db
        .get_pool(&PoolKey::new("e621", "missing").unwrap())
        .unwrap()
        .is_none());
}

#[test]
fn invalid_pool_snapshot_does_not_partially_replace_previous_snapshot() {
    let mut db = CacheStore::in_memory().unwrap();
    let good = pool("e621", "1", &["30", "2"]);
    db.reconcile_pool(&good).unwrap();
    for bad in [
        pool("e621", "1", &["10", "10"]),
        pool("e621", "1", &["10", ""]),
    ] {
        assert!(db.reconcile_pool(&bad).is_err());
        assert_eq!(db.get_pool(&good.key).unwrap(), Some(good.clone()));
    }
}

#[test]
fn post_upsert_preserves_composite_identity_and_json_metadata() {
    let mut db = CacheStore::in_memory().unwrap();
    let mut a = MediaItem::new(PostKey::new("e621", "42").unwrap());
    a.description = "upstream description".into();
    a.tags = serde_json::from_str(r#"{"contributor":["helper"],"future":["tag"]}"#).unwrap();
    let mut b = MediaItem::new(PostKey::new("other", "42").unwrap());
    b.title = "different source".into();
    db.upsert_posts(&[a.clone(), b.clone()]).unwrap();
    assert_eq!(db.get_post(&a.key).unwrap(), Some(a.clone()));
    assert_eq!(db.get_post(&b.key).unwrap(), Some(b.clone()));
    a.title = "refreshed".into();
    db.upsert_posts(&[a.clone()]).unwrap();
    assert_eq!(db.get_post(&a.key).unwrap(), Some(a));
    assert_eq!(db.get_post(&b.key).unwrap(), Some(b));
    assert_eq!(
        db.get_post(&PostKey::new("e621", "missing").unwrap())
            .unwrap(),
        None
    );
}
