use furchive_app::legacy::{self, ImportSelection, QueuePreview};
use furchive_engine::storage::StateStore;
use rusqlite::Connection;
use serde_json::json;
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

fn files(root: &Path) -> BTreeMap<PathBuf, Vec<u8>> {
    fn visit(root: &Path, at: &Path, out: &mut BTreeMap<PathBuf, Vec<u8>>) {
        for e in std::fs::read_dir(at).unwrap() {
            let p = e.unwrap().path();
            if p.is_dir() {
                visit(root, &p, out);
            } else {
                out.insert(
                    p.strip_prefix(root).unwrap().into(),
                    std::fs::read(p).unwrap(),
                );
            }
        }
    }
    let mut out = BTreeMap::new();
    visit(root, root, &mut out);
    out
}

#[test]
fn exact_csharp_full_schema_import_excludes_queue_secrets_and_preview_authority() {
    let old = tempfile::tempdir().unwrap();
    std::fs::create_dir_all(old.path().join("cache/e621")).unwrap();
    std::fs::create_dir(old.path().join("art")).unwrap();
    std::fs::write(old.path().join("art/keep.bin"), b"synthetic-art-verifier").unwrap();
    let c = Connection::open(old.path().join("cache/e621/pinned_pools.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE pinned_pools(id INTEGER PRIMARY KEY,name TEXT NOT NULL,post_count INTEGER NOT NULL); INSERT INTO pinned_pools VALUES(91,'Verifier pool',2);").unwrap();
    drop(c);
    let c = Connection::open(old.path().join("cache/e621/last_session.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);")
        .unwrap();
    c.execute("INSERT INTO meta VALUES('last_session',?1)", [json!({"IsPoolMode":true,"PoolId":91,"Include":["fox"],"Exclude":["comic"],"RatingFilterIndex":3,"Page":4}).to_string()]).unwrap();
    drop(c);
    let c = Connection::open(old.path().join("downloads.sqlite")).unwrap();
    c.execute_batch("CREATE TABLE downloads(id TEXT PRIMARY KEY,source TEXT,media_id TEXT,title TEXT,artist TEXT,destination_path TEXT,status TEXT,bytes_downloaded INTEGER,total_bytes INTEGER,queued_at TEXT,started_at TEXT,completed_at TEXT,error_message TEXT); INSERT INTO downloads VALUES('queued','e621','8','synthetic','synthetic','never-open-verifier','Queued',1,8,'2026-01-01',NULL,NULL,'VERIFIER_QUEUE_SECRET'); INSERT INTO downloads VALUES('complete','e621','9','synthetic','synthetic','never-open-verifier','Completed',8,8,'2026-01-01',NULL,NULL,NULL);").unwrap();
    drop(c);
    let new = tempfile::tempdir().unwrap();
    let forbidden_hint = new.path().join("must-not-be-created");
    std::fs::write(old.path().join("settings.json"), json!({"SavedSearches":json!([{"Name":"Verifier search","IncludeTags":["fox"],"ExcludeTags":["comic"],"RatingFilterIndex":3}]).to_string(),"E621Username":"VERIFIER_USER_SECRET","E621ApiKey":"VERIFIER_API_SECRET","LoadLastSessionEnabled":true,"DefaultDownloadDirectory":forbidden_hint,"ConcurrentDownloads":4}).to_string()).unwrap();
    let before = files(old.path());
    let inventory = legacy::preview(old.path()).unwrap();
    assert_eq!(inventory.queue(), &QueuePreview::Quarantined { records: 2 });
    assert_eq!(inventory.session().unwrap().page, 4);
    assert_eq!(inventory.settings().requested_session_restore, Some(true));
    let state = new.path().join("state.sqlite");
    drop(StateStore::open(&state).unwrap());
    let before_c = Connection::open(&state).unwrap();
    let schema: Vec<String> = before_c
        .prepare("SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name")
        .unwrap()
        .query_map([], |r| r.get(0))
        .unwrap()
        .map(Result::unwrap)
        .collect();
    drop(before_c);
    let import = || {
        inventory
            .select(ImportSelection {
                search_ids: inventory.searches().iter().map(|s| s.id.clone()).collect(),
                pool_ids: vec!["91".into()],
            })
            .unwrap()
            .confirm(inventory.content_digest())
            .unwrap()
    };
    let report = legacy::import_into(import(), &state).unwrap();
    assert_eq!((report.imported_searches, report.imported_pins), (1, 1));
    let c = Connection::open(&state).unwrap();
    let after_schema: Vec<String> = c
        .prepare("SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name")
        .unwrap()
        .query_map([], |r| r.get(0))
        .unwrap()
        .map(Result::unwrap)
        .collect();
    let additions: Vec<_> = after_schema
        .iter()
        .filter(|s| !schema.contains(s))
        .collect();
    assert_eq!(additions, vec!["legacy_import_v1"]);
    let receipts: Vec<(String, String)> = c
        .prepare("SELECT kind,item_id FROM legacy_import_v1 ORDER BY kind")
        .unwrap()
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))
        .unwrap()
        .map(Result::unwrap)
        .collect();
    assert_eq!(receipts.len(), 2);
    assert_eq!(receipts[0], ("e621-pin".into(), "91".into()));
    let mut store = StateStore::open(&state).unwrap();
    let mut edited = store.saved_searches().unwrap().remove(0);
    edited.name = "Edited current search".into();
    store.save_search(&edited).unwrap();
    let second = legacy::import_into(import(), &state).unwrap();
    assert_eq!((second.skipped_searches, second.skipped_pins), (1, 1));
    assert_eq!(store.saved_searches().unwrap(), vec![edited]);
    drop(store);
    drop(c);
    for bytes in files(new.path()).values() {
        for secret in [
            b"VERIFIER_QUEUE_SECRET".as_slice(),
            b"VERIFIER_USER_SECRET",
            b"VERIFIER_API_SECRET",
            b"never-open-verifier",
            b"must-not-be-created",
        ] {
            assert!(!bytes.windows(secret.len()).any(|w| w == secret));
        }
    }
    assert!(!forbidden_hint.exists());
    assert_eq!(files(old.path()), before);
}
