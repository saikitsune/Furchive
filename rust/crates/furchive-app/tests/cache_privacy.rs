use furchive_app::Database;
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey};
use furchive_engine::storage::SavedSearch;

#[tokio::test]
async fn clear_cache_preserves_user_state_and_rejects_old_cache_writes() {
    let directory = tempfile::tempdir().unwrap();
    let db = Database::open(directory.path()).await.unwrap();
    db.save_search(SavedSearch {
        id: "keep".into(),
        name: "Keep me".into(),
        query: Default::default(),
    })
    .await
    .unwrap();
    db.set_pin(PoolKey::new("e621", "7").unwrap(), true)
        .await
        .unwrap();
    std::fs::write(
        directory.path().join("artwork.png"),
        b"owned artwork sentinel",
    )
    .unwrap();
    std::fs::write(
        directory.path().join("downloads.sqlite"),
        b"download history sentinel",
    )
    .unwrap();
    std::fs::write(directory.path().join("settings.json"), b"settings sentinel").unwrap();
    let post = MediaItem::new(PostKey::new("e621", "42").unwrap());
    let pool = Pool::new(PoolKey::new("e621", "7").unwrap(), "Pool".into());
    let old = db.cache_epoch().await.unwrap();
    db.store_posts_at(old, vec![post.clone()]).await.unwrap();
    db.store_pool_at(old, pool.clone()).await.unwrap();
    db.clear_cache().await.unwrap();
    assert!(db.store_posts_at(old, vec![post.clone()]).await.is_err());
    assert!(db.store_pool_at(old, pool.clone()).await.is_err());
    assert_eq!(db.searches().await.unwrap()[0].id, "keep");
    assert_eq!(db.pins().await.unwrap()[0].id(), "7");
    db.shutdown().await.unwrap();
    let cache = rusqlite::Connection::open(directory.path().join("cache.sqlite")).unwrap();
    for table in ["posts", "pools", "pool_posts"] {
        let count: u32 = cache
            .query_row(&format!("SELECT count(*) FROM {table}"), [], |r| r.get(0))
            .unwrap();
        assert_eq!(count, 0);
    }
    drop(cache);
    for (name, contents) in [
        ("artwork.png", b"owned artwork sentinel".as_slice()),
        ("downloads.sqlite", b"download history sentinel"),
        ("settings.json", b"settings sentinel"),
    ] {
        assert_eq!(
            std::fs::read(directory.path().join(name)).unwrap(),
            contents
        );
    }
    let reopened = Database::open(directory.path()).await.unwrap();
    assert_eq!(reopened.searches().await.unwrap().len(), 1);
    assert_eq!(reopened.pins().await.unwrap().len(), 1);
    let current = reopened.cache_epoch().await.unwrap();
    reopened.store_posts_at(current, vec![post]).await.unwrap();
    reopened.shutdown().await.unwrap();
}

#[tokio::test]
async fn cache_clear_failure_rolls_back_all_metadata_and_keeps_epoch() {
    let dir = tempfile::tempdir().unwrap();
    let db = Database::open(dir.path()).await.unwrap();
    let epoch = db.cache_epoch().await.unwrap();
    db.store_posts_at(
        epoch,
        vec![MediaItem::new(PostKey::new("e621", "42").unwrap())],
    )
    .await
    .unwrap();
    let connection = rusqlite::Connection::open(dir.path().join("cache.sqlite")).unwrap();
    connection.execute_batch("CREATE TRIGGER deny_clear BEFORE DELETE ON posts BEGIN SELECT RAISE(ABORT,'owned clear refusal'); END;").unwrap();
    assert!(db.clear_cache().await.is_err());
    assert_eq!(db.cache_epoch().await.unwrap(), epoch);
    assert_eq!(
        connection
            .query_row("SELECT count(*) FROM posts", [], |r| r.get::<_, u32>(0))
            .unwrap(),
        1
    );
    db.store_posts_at(
        epoch,
        vec![MediaItem::new(PostKey::new("e621", "43").unwrap())],
    )
    .await
    .unwrap();
    db.shutdown().await.unwrap();
}

#[tokio::test]
async fn cache_clear_boundary_waits_for_admitted_operations_and_revokes_media() {
    let boundary = furchive_app::CacheBoundary::default();
    let cache = furchive_app::media_cache::MediaCache::new(1024).unwrap();
    let post = MediaItem::new(PostKey::new("e621", "42").unwrap());
    cache.authorize(&[post]).unwrap();
    let reader = boundary.read().await;
    let mut clear = Box::pin(async {
        let _guard = boundary.write().await;
        cache.begin_page().unwrap();
    });
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(20), &mut clear)
            .await
            .is_err()
    );
    assert!(cache.authorized_post("42").is_ok());
    drop(reader);
    clear.await;
    assert!(cache.authorized_post("42").is_err());
    assert_eq!(cache.resident_bytes().unwrap(), 0);
}
