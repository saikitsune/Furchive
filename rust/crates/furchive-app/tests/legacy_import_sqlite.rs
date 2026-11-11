use furchive_app::legacy::{self, ImportSelection, QueuePreview};
use furchive_engine::storage::StateStore;
use rusqlite::Connection;
use serde_json::json;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

fn pin_db(root: &Path) -> Connection {
    std::fs::create_dir_all(root.join("cache/e621")).unwrap();
    let c = Connection::open(root.join("cache/e621/pinned_pools.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE pinned_pools(id INTEGER PRIMARY KEY,name TEXT NOT NULL,post_count INTEGER NOT NULL); INSERT INTO pinned_pools VALUES(42,'Synthetic pool',3);").unwrap();
    c
}
fn settings(root: &Path) {
    std::fs::write(
        root.join("settings.json"),
        json!({"SavedSearches":json!([
        {"Name":"Synthetic search","IncludeTags":["otter"],"RatingFilterIndex":3}
    ]).to_string(), "E621ApiKey":"fixture-key-not-retained"})
        .to_string(),
    )
    .unwrap();
}
fn selected(p: &legacy::Inventory) -> legacy::ConfirmedImport {
    p.select(ImportSelection {
        search_ids: p.searches().iter().map(|s| s.id.clone()).collect(),
        pool_ids: p.pins().iter().map(|s| s.key.id().to_owned()).collect(),
    })
    .unwrap()
    .confirm(p.content_digest())
    .unwrap()
}
fn tree(root: &Path) -> BTreeMap<PathBuf, Vec<u8>> {
    fn visit(root: &Path, at: &Path, result: &mut BTreeMap<PathBuf, Vec<u8>>) {
        for entry in std::fs::read_dir(at).unwrap() {
            let path = entry.unwrap().path();
            if path.is_dir() {
                visit(root, &path, result);
            } else {
                result.insert(
                    path.strip_prefix(root).unwrap().to_owned(),
                    std::fs::read(path).unwrap(),
                );
            }
        }
    }
    let mut result = BTreeMap::new();
    visit(root, root, &mut result);
    result
}

#[test]
fn real_sqlite_pins_session_and_queue_are_previewed_offline() {
    let old = tempfile::tempdir().unwrap();
    drop(pin_db(old.path()));
    let s = Connection::open(old.path().join("cache/e621/last_session.sqlite")).unwrap();
    s.execute_batch("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);")
        .unwrap();
    s.execute("INSERT INTO meta VALUES('last_session',?1)", [json!({"IsPoolMode":true,"PoolId":42,"Include":["otter"],"Exclude":[],"RatingFilterIndex":3,"Page":2}).to_string()]).unwrap();
    drop(s);
    let q = Connection::open(old.path().join("downloads.sqlite")).unwrap();
    q.execute_batch("CREATE TABLE downloads(id TEXT PRIMARY KEY,destination_path TEXT,status TEXT); INSERT INTO downloads VALUES('one','never-authorized','Queued');").unwrap();
    drop(q);
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.pins()[0].key.id(), "42");
    assert_eq!(p.session().unwrap().page, 2);
    assert_eq!(p.session().unwrap().pool.as_ref().unwrap().id(), "42");
    assert_eq!(p.queue(), &QueuePreview::Quarantined { records: 1 });
    assert!(!format!("{p:?}").contains("never-authorized"));
    let new = tempfile::tempdir().unwrap();
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let report = legacy::import_into(selected(&p), &path).unwrap();
    assert_eq!(report.imported_pins, 1);
    let store = StateStore::open(&path).unwrap();
    assert_eq!(store.pinned_pools().unwrap()[0].id(), "42");
    assert_eq!(
        legacy::import_into(selected(&p), &path)
            .unwrap()
            .skipped_pins,
        1
    );
    assert_eq!(store.pinned_pools().unwrap().len(), 1);
}

#[test]
fn foreign_destination_with_wal_is_refused_without_sidecar_mutation() {
    let old = tempfile::tempdir().unwrap();
    settings(old.path());
    let p = legacy::preview(old.path()).unwrap();
    let target = tempfile::tempdir().unwrap();
    let path = target.path().join("foreign.sqlite");
    let c = Connection::open(&path).unwrap();
    c.execute_batch("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE private_stuff(x); INSERT INTO private_stuff VALUES(1);").unwrap();
    let main = std::fs::read(&path).unwrap();
    let wal = std::fs::read(target.path().join("foreign.sqlite-wal")).unwrap();
    drop(c);
    std::fs::write(&path, main).unwrap();
    std::fs::write(target.path().join("foreign.sqlite-wal"), wal).unwrap();
    let before = tree(target.path());
    assert!(legacy::import_into(selected(&p), &path).is_err());
    assert_eq!(tree(target.path()), before);
}

#[test]
fn import_refuses_any_destination_inside_the_legacy_tree() {
    let old = tempfile::tempdir().unwrap();
    settings(old.path());
    let state = old.path().join("rust-state.sqlite");
    drop(StateStore::open(&state).unwrap());
    let before = tree(old.path());
    let p = legacy::preview(old.path()).unwrap();
    assert!(legacy::import_into(selected(&p), &state).is_err());
    assert_eq!(tree(old.path()), before);
}

#[test]
fn old_main_wal_shm_settings_and_art_remain_byte_exact() {
    let old = tempfile::tempdir().unwrap();
    settings(old.path());
    let c = pin_db(old.path());
    c.execute_batch("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO pinned_pools VALUES(43,'WAL only',1);").unwrap();
    assert!(
        old.path()
            .join("cache/e621/pinned_pools.sqlite-wal")
            .exists()
    );
    std::fs::create_dir_all(old.path().join("artwork")).unwrap();
    std::fs::write(old.path().join("artwork/keep.bin"), [0u8, 255, 11, 3]).unwrap();
    let before = tree(old.path());
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.pins().len(), 2, "WAL records must not be silently lost");
    let new = tempfile::tempdir().unwrap();
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    legacy::import_into(selected(&p), &path).unwrap();
    assert_eq!(tree(old.path()), before);
    assert!(!format!("{p:?}").contains("fixture-key-not-retained"));
    drop(c);
}

#[test]
fn ignored_admitted_write_rolls_back_instead_of_recording_success() {
    let old = tempfile::tempdir().unwrap();
    settings(old.path());
    drop(pin_db(old.path()));
    let p = legacy::preview(old.path()).unwrap();
    let new = tempfile::tempdir().unwrap();
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let c = Connection::open(&path).unwrap();
    c.execute_batch(
        "CREATE TRIGGER ignore_pin BEFORE INSERT ON pinned_pools BEGIN SELECT RAISE(IGNORE); END;",
    )
    .unwrap();
    assert!(legacy::import_into(selected(&p), &path).is_err());
    assert!(
        StateStore::open(&path)
            .unwrap()
            .saved_searches()
            .unwrap()
            .is_empty()
    );
    c.execute_batch("DROP TRIGGER ignore_pin;").unwrap();
    assert_eq!(
        legacy::import_into(selected(&p), &path)
            .unwrap()
            .imported_pins,
        1
    );
}

#[test]
fn admitted_pin_failure_rolls_back_search_pin_and_receipts() {
    let old = tempfile::tempdir().unwrap();
    settings(old.path());
    drop(pin_db(old.path()));
    let p = legacy::preview(old.path()).unwrap();
    let new = tempfile::tempdir().unwrap();
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let c = Connection::open(&path).unwrap();
    c.execute_batch("CREATE TRIGGER reject_pin BEFORE INSERT ON pinned_pools BEGIN SELECT RAISE(ABORT,'synthetic-write-failure-secret'); END;").unwrap();
    let error = legacy::import_into(selected(&p), &path).unwrap_err();
    assert!(!error.contains("synthetic-write-failure-secret"));
    let store = StateStore::open(&path).unwrap();
    assert!(store.saved_searches().unwrap().is_empty());
    assert!(store.pinned_pools().unwrap().is_empty());
    c.execute_batch("DROP TRIGGER reject_pin;").unwrap();
    let report = legacy::import_into(selected(&p), &path).unwrap();
    assert_eq!(report.imported_searches, 1);
    assert_eq!(report.imported_pins, 1);
}

#[test]
fn malformed_wal_never_silently_loses_committed_pins() {
    let old = tempfile::tempdir().unwrap();
    let c = pin_db(old.path());
    c.execute_batch("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO pinned_pools VALUES(43,'WAL only',1);").unwrap();
    let main = std::fs::read(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
    let mut wal = std::fs::read(old.path().join("cache/e621/pinned_pools.sqlite-wal")).unwrap();
    drop(c);
    wal[0] ^= 0xff;
    std::fs::write(old.path().join("cache/e621/pinned_pools.sqlite"), main).unwrap();
    std::fs::write(old.path().join("cache/e621/pinned_pools.sqlite-wal"), wal).unwrap();
    assert!(
        legacy::preview(old.path()).is_err(),
        "malformed WAL must not produce a partial successful inventory"
    );
}

#[test]
fn truncated_and_checksum_corrupt_wal_fail_closed() {
    let old = tempfile::tempdir().unwrap();
    let c = pin_db(old.path());
    c.execute_batch("PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; INSERT INTO pinned_pools VALUES(43,'WAL only',1);").unwrap();
    let main = std::fs::read(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
    let wal = std::fs::read(old.path().join("cache/e621/pinned_pools.sqlite-wal")).unwrap();
    drop(c);
    for mode in 0..3 {
        let mut corrupt = wal.clone();
        match mode {
            0 => {
                corrupt.pop();
            }
            1 => {
                let last = corrupt.len() - 1;
                corrupt[last] ^= 1;
            }
            _ => {
                corrupt[40] ^= 1;
            }
        }
        std::fs::write(old.path().join("cache/e621/pinned_pools.sqlite"), &main).unwrap();
        std::fs::write(
            old.path().join("cache/e621/pinned_pools.sqlite-wal"),
            &corrupt,
        )
        .unwrap();
        let before = tree(old.path());
        assert!(legacy::preview(old.path()).is_err());
        assert!(tree(old.path()) == before);
    }
}

#[test]
fn source_pin_duplicates_fail_closed() {
    let old = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(old.path().join("cache/e621")).unwrap();
    let c = Connection::open(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE pinned_pools(id INTEGER,name TEXT,post_count INTEGER); INSERT INTO pinned_pools VALUES(42,'one',1),(42,'two',2);").unwrap();
    drop(c);
    assert!(legacy::preview(old.path()).is_err());
}

#[test]
fn source_generated_columns_are_not_evaluated() {
    let old = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(old.path().join("cache/e621")).unwrap();
    let c = Connection::open(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE pinned_pools(id INTEGER PRIMARY KEY,name TEXT GENERATED ALWAYS AS ('generated') VIRTUAL,post_count INTEGER); INSERT INTO pinned_pools(id,post_count) VALUES(1,1);").unwrap();
    drop(c);
    assert!(
        legacy::preview(old.path()).is_err(),
        "only inert stored legacy columns may be read"
    );
}

#[test]
fn source_views_and_virtual_tables_are_not_executed() {
    for schema in [
        "CREATE VIEW pinned_pools AS SELECT randomblob(1000000000) AS name,1 AS id,1 AS post_count;",
        "CREATE VIRTUAL TABLE pinned_pools USING fts5(name);",
    ] {
        let old = tempfile::tempdir().unwrap();
        std::fs::create_dir_all(old.path().join("cache/e621")).unwrap();
        let c = Connection::open(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
        c.execute_batch(schema).unwrap();
        drop(c);
        let before = tree(old.path());
        assert!(legacy::preview(old.path()).is_err());
        assert_eq!(tree(old.path()), before);
    }
}

#[test]
fn oversized_pin_values_are_rejected_without_losing_valid_rows() {
    let old = tempfile::tempdir().unwrap();
    let c = pin_db(old.path());
    c.execute(
        "INSERT INTO pinned_pools VALUES(44,?1,1)",
        ["x".repeat(65536)],
    )
    .unwrap();
    c.execute_batch("INSERT INTO pinned_pools VALUES(-1,'bad',1);")
        .unwrap();
    drop(c);
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.pins().len(), 1);
    assert_eq!(
        p.warnings()
            .iter()
            .filter(|w| w.contains("rejected"))
            .count(),
        2
    );
}

#[test]
fn sqlite_pin_queue_and_database_limits_fail_closed() {
    let old = tempfile::tempdir().unwrap();
    let c = pin_db(old.path());
    c.execute_batch("WITH RECURSIVE n(x) AS (VALUES(100) UNION ALL SELECT x+1 FROM n WHERE x<1100) INSERT INTO pinned_pools SELECT x,'bounded',1 FROM n;").unwrap();
    drop(c);
    assert!(legacy::preview(old.path()).is_err());
    let queue_root = tempfile::tempdir().unwrap();
    let c = Connection::open(queue_root.path().join("downloads.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE downloads(id); WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<1001) INSERT INTO downloads SELECT x FROM n;").unwrap();
    drop(c);
    assert!(legacy::preview(queue_root.path()).is_err());
    let large = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(large.path().join("cache/e621")).unwrap();
    std::fs::File::create(large.path().join("cache/e621/pinned_pools.sqlite"))
        .unwrap()
        .set_len((legacy::MAX_DATABASE_BYTES + 1) as u64)
        .unwrap();
    assert!(legacy::preview(large.path()).is_err());
}

#[test]
fn session_container_errors_fail_but_invalid_fields_warn() {
    let old = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(old.path().join("cache/e621")).unwrap();
    let c = Connection::open(old.path().join("cache/e621/last_session.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);")
        .unwrap();
    for value in ["[]", "null", "{", "\"fixture-secret\""] {
        c.execute(
            "INSERT OR REPLACE INTO meta VALUES('last_session',?1)",
            [value],
        )
        .unwrap();
        let result = legacy::preview(old.path());
        assert!(result.is_err());
        assert!(!format!("{result:?}").contains("fixture-secret"));
    }
    for v in [
        json!({"Page":0}),
        json!({"RatingFilterIndex":-1}),
        json!({"Include":[7]}),
        json!({"IsPoolMode":true,"PoolId":null}),
    ] {
        c.execute(
            "INSERT OR REPLACE INTO meta VALUES('last_session',?1)",
            [v.to_string()],
        )
        .unwrap();
        let p = legacy::preview(old.path()).unwrap();
        assert!(p.session().is_none());
        assert!(p.warnings().iter().any(|w| w.contains("session rejected")));
    }
}

#[test]
fn source_triggers_and_other_tables_never_run_or_leak() {
    let old = tempfile::tempdir().unwrap();
    let c = pin_db(old.path());
    c.execute_batch("CREATE TABLE credentials(secret TEXT); INSERT INTO credentials VALUES('synthetic-db-secret'); CREATE TRIGGER destructive_old_trigger AFTER UPDATE ON pinned_pools BEGIN DELETE FROM credentials; END;").unwrap();
    drop(c);
    let before = tree(old.path());
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.pins().len(), 1);
    assert!(!format!("{p:?}").contains("synthetic-db-secret"));
    assert_eq!(tree(old.path()), before);
}

#[test]
fn pin_collisions_and_deleted_imported_pins_preserve_current_decisions() {
    let old = tempfile::tempdir().unwrap();
    drop(pin_db(old.path()));
    let p = legacy::preview(old.path()).unwrap();
    let new = tempfile::tempdir().unwrap();
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let mut store = StateStore::open(&path).unwrap();
    let unrelated = furchive_domain::PoolKey::new("other", "42").unwrap();
    store.set_pin(&unrelated, true).unwrap();
    let pin = &p.pins()[0].key;
    store.set_pin(pin, true).unwrap();
    assert_eq!(
        legacy::import_into(selected(&p), &path)
            .unwrap()
            .skipped_pins,
        1
    );
    store.set_pin(pin, false).unwrap();
    assert_eq!(
        legacy::import_into(selected(&p), &path)
            .unwrap()
            .imported_pins,
        1
    );
    store.set_pin(pin, false).unwrap();
    assert_eq!(
        legacy::import_into(selected(&p), &path)
            .unwrap()
            .skipped_pins,
        1
    );
    assert_eq!(store.pinned_pools().unwrap(), vec![unrelated]);
}

#[test]
fn rollback_journal_refuses_automatic_recovery_without_mutation() {
    let old = tempfile::tempdir().unwrap();
    drop(pin_db(old.path()));
    std::fs::write(
        old.path().join("cache/e621/pinned_pools.sqlite-journal"),
        b"synthetic-hot-journal",
    )
    .unwrap();
    let before = tree(old.path());
    assert!(legacy::preview(old.path()).is_err());
    assert_eq!(tree(old.path()), before);
}
