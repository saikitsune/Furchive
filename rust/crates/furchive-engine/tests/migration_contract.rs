use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, SearchQuery};
use furchive_engine::storage::{
    CacheStore, SavedSearch, StateStore, CACHE_APPLICATION_ID, SCHEMA_VERSION, STATE_APPLICATION_ID,
};
use rusqlite::Connection;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

static SEQUENCE: AtomicU64 = AtomicU64::new(0);

#[test]
fn corrupted_metadata_cannot_change_a_requested_identity_or_silently_drop_rows() {
    let file = TestDb::new();
    let mut store = CacheStore::open(&file.0).unwrap();
    let item = MediaItem::new(PostKey::new("e621", "42").unwrap());
    store.upsert_posts(std::slice::from_ref(&item)).unwrap();
    let mut pool = Pool::new(PoolKey::new("e621", "1").unwrap(), "pool".into());
    pool.post_ids = vec!["42".into()];
    store.reconcile_pool(&pool).unwrap();
    let foreign = MediaItem::new(PostKey::new("other", "42").unwrap());
    let conn = Connection::open(&file.0).unwrap();
    conn.execute(
        "UPDATE posts SET data_json=?1",
        [serde_json::to_string(&foreign).unwrap()],
    )
    .unwrap();
    assert!(store.get_post(&item.key).is_err());
    assert!(store.pool_entries(&pool.key).is_err());
    let foreign_pool = Pool::new(PoolKey::new("other", "1").unwrap(), "foreign".into());
    conn.execute(
        "UPDATE pools SET data_json=?1",
        [serde_json::to_string(&foreign_pool).unwrap()],
    )
    .unwrap();
    assert!(store.get_pool(&pool.key).is_err());
    conn.execute("UPDATE posts SET data_json='{}'", []).unwrap();
    assert!(store.get_post(&item.key).is_err());
}

#[test]
fn database_failure_rolls_back_post_batch_and_pool_metadata_with_membership() {
    let file = TestDb::new();
    let mut db = CacheStore::open(&file.0).unwrap();
    let first = MediaItem::new(PostKey::new("e621", "1").unwrap());
    let second = MediaItem::new(PostKey::new("e621", "2").unwrap());
    db.upsert_posts(std::slice::from_ref(&first)).unwrap();
    let mut pool = Pool::new(PoolKey::new("e621", "1").unwrap(), "before".into());
    pool.post_ids = vec!["1".into()];
    db.reconcile_pool(&pool).unwrap();
    let conn = Connection::open(&file.0).unwrap();
    conn.execute_batch("CREATE TRIGGER fail_post BEFORE INSERT ON posts WHEN NEW.post_id='2' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;
CREATE TRIGGER fail_pool BEFORE INSERT ON pool_posts WHEN NEW.post_id='2' BEGIN SELECT RAISE(ABORT,'fixture failure'); END;").unwrap();
    let mut changed = first.clone();
    changed.title = "must roll back".into();
    assert!(db.upsert_posts(&[changed, second]).is_err());
    assert_eq!(db.get_post(&first.key).unwrap(), Some(first));
    let mut changed_pool = pool.clone();
    changed_pool.name = "must roll back".into();
    changed_pool.post_ids = vec!["3".into(), "2".into()];
    assert!(db.reconcile_pool(&changed_pool).is_err());
    assert_eq!(db.get_pool(&pool.key).unwrap(), Some(pool.clone()));
    assert_eq!(db.pool_entries(&pool.key).unwrap()[0].key.id(), "1");
}

#[test]
fn unsupported_saved_search_record_versions_and_invalid_json_are_not_hidden() {
    let file = TestDb::new();
    let mut state = StateStore::open(&file.0).unwrap();
    let search = SavedSearch {
        id: "valid".into(),
        name: "valid".into(),
        query: SearchQuery::default(),
    };
    state.save_search(&search).unwrap();
    let conn = Connection::open(&file.0).unwrap();
    conn.execute("UPDATE saved_searches SET record_version=2", [])
        .unwrap();
    assert!(state.saved_searches().is_err());
    conn.execute(
        "UPDATE saved_searches SET record_version=1,query_json='[]'",
        [],
    )
    .unwrap();
    assert!(state.saved_searches().is_err());
}

struct TestDb(PathBuf);
impl TestDb {
    fn new() -> Self {
        let dir = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("test-artifacts/databases");
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join(format!(
            "{}-{}.sqlite",
            std::process::id(),
            SEQUENCE.fetch_add(1, Ordering::Relaxed)
        ));
        Self(path)
    }
}
impl Drop for TestDb {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

#[test]
fn disk_reopen_preserves_both_stores_and_cache_clear_cannot_erase_user_state() {
    let cache_file = TestDb::new();
    let state_file = TestDb::new();
    let key = PoolKey::new("e621", "1").unwrap();
    let item = MediaItem::new(PostKey::new("e621", "42").unwrap());
    let mut pool = Pool::new(key.clone(), "pool".into());
    pool.post_ids = vec!["42".into()];
    let search = SavedSearch {
        id: "1".into(),
        name: "test".into(),
        query: SearchQuery::default(),
    };
    {
        let mut cache = CacheStore::open(&cache_file.0).unwrap();
        cache.upsert_posts(std::slice::from_ref(&item)).unwrap();
        cache.reconcile_pool(&pool).unwrap();
        let mut state = StateStore::open(&state_file.0).unwrap();
        state.set_pin(&key, true).unwrap();
        state.save_search(&search).unwrap();
    }
    let mut cache = CacheStore::open(&cache_file.0).unwrap();
    assert_eq!(cache.get_post(&item.key).unwrap(), Some(item));
    assert_eq!(cache.get_pool(&key).unwrap(), Some(pool));
    cache.clear().unwrap();
    assert!(cache.pool_entries(&key).unwrap().is_empty());
    assert!(cache.get_pool(&key).unwrap().is_none());
    let state = StateStore::open(&state_file.0).unwrap();
    assert_eq!(state.saved_searches().unwrap(), [search]);
    assert_eq!(state.pinned_pools().unwrap(), [key]);
    for (path, app_id) in [
        (&cache_file.0, CACHE_APPLICATION_ID),
        (&state_file.0, STATE_APPLICATION_ID),
    ] {
        let conn = Connection::open(path).unwrap();
        assert_eq!(
            conn.query_row("PRAGMA application_id", [], |r| r.get::<_, i64>(0))
                .unwrap(),
            app_id
        );
        assert_eq!(
            conn.query_row("PRAGMA user_version", [], |r| r.get::<_, u32>(0))
                .unwrap(),
            SCHEMA_VERSION
        );
    }
}

#[test]
fn wrong_kind_future_schema_and_legacy_files_are_refused_without_claiming_them() {
    let file = TestDb::new();
    drop(CacheStore::open(&file.0).unwrap());
    assert!(StateStore::open(&file.0).is_err());
    let conn = Connection::open(&file.0).unwrap();
    conn.pragma_update(None, "user_version", SCHEMA_VERSION + 1)
        .unwrap();
    drop(conn);
    assert!(CacheStore::open(&file.0).is_err());
    let conn = Connection::open(&file.0).unwrap();
    assert_eq!(
        conn.query_row("PRAGMA user_version", [], |r| r.get::<_, u32>(0))
            .unwrap(),
        SCHEMA_VERSION + 1
    );
    let legacy = Connection::open_in_memory().unwrap();
    legacy
        .execute_batch("CREATE TABLE posts(id TEXT PRIMARY KEY, source TEXT);")
        .unwrap();
    assert!(CacheStore::from_connection(legacy).is_err());
}

#[test]
fn upgrades_version_one_data_and_is_idempotent() {
    let file = TestDb::new();
    let conn = Connection::open(&file.0).unwrap();
    conn.execute_batch(include_str!("../migrations/cache_0001.sql"))
        .unwrap();
    conn.pragma_update(None, "application_id", CACHE_APPLICATION_ID)
        .unwrap();
    conn.pragma_update(None, "user_version", 1).unwrap();
    let mut item = MediaItem::new(PostKey::new("e621", "42").unwrap());
    item.fetched_at = Some(1234);
    conn.execute(
        "INSERT INTO posts(source,post_id,data_json) VALUES(?1,?2,?3)",
        rusqlite::params!["e621", "42", serde_json::to_string(&item).unwrap()],
    )
    .unwrap();
    drop(conn);
    for _ in 0..2 {
        let store = CacheStore::open(&file.0).unwrap();
        assert_eq!(store.get_post(&item.key).unwrap(), Some(item.clone()));
    }
    let conn = Connection::open(&file.0).unwrap();
    assert_eq!(
        conn.query_row("SELECT fetched_at FROM posts", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        1234
    );
    let state_conn = Connection::open_in_memory().unwrap();
    state_conn
        .execute_batch(include_str!("../migrations/state_0001.sql"))
        .unwrap();
    state_conn
        .pragma_update(None, "application_id", STATE_APPLICATION_ID)
        .unwrap();
    state_conn.pragma_update(None, "user_version", 1).unwrap();
    state_conn
        .execute(
            "INSERT INTO saved_searches(id,name,query_json) VALUES('1','v1',?1)",
            [serde_json::to_string(&SearchQuery::default()).unwrap()],
        )
        .unwrap();
    assert_eq!(
        StateStore::from_connection(state_conn)
            .unwrap()
            .saved_searches()
            .unwrap()[0]
            .name,
        "v1"
    );
}

#[test]
fn failed_upgrade_rolls_back_all_schema_steps_and_version() {
    let file = TestDb::new();
    let conn = Connection::open(&file.0).unwrap();
    conn.execute_batch(include_str!("../migrations/cache_0001.sql"))
        .unwrap();
    conn.execute_batch("ALTER TABLE pools ADD COLUMN fetched_at INTEGER;")
        .unwrap();
    conn.pragma_update(None, "application_id", CACHE_APPLICATION_ID)
        .unwrap();
    conn.pragma_update(None, "user_version", 1).unwrap();
    drop(conn);
    assert!(CacheStore::open(&file.0).is_err());
    let conn = Connection::open(&file.0).unwrap();
    assert_eq!(
        conn.query_row("PRAGMA user_version", [], |r| r.get::<_, u32>(0))
            .unwrap(),
        1
    );
    let mut stmt = conn.prepare("PRAGMA table_info(posts)").unwrap();
    let names: Vec<String> = stmt
        .query_map([], |r| r.get(1))
        .unwrap()
        .collect::<rusqlite::Result<_>>()
        .unwrap();
    assert!(!names.contains(&"fetched_at".to_owned()));
}
