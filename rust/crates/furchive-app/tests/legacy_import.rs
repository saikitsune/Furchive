use furchive_app::legacy::{self, ImportSelection};
use furchive_engine::storage::StateStore;
use serde_json::json;

fn settings(root: &std::path::Path) {
    let value = json!({"SavedSearches": json!([
        {"Name":"Foxes","IncludeTags":["fox"],"ExcludeTags":["comic"],"RatingFilterIndex":3}
    ]).to_string(), "E621ApiKey":"synthetic-never-retain", "E621Username":"synthetic-user"});
    std::fs::write(
        root.join("settings.json"),
        serde_json::to_vec(&value).unwrap(),
    )
    .unwrap();
}

#[test]
fn preview_inventories_allowlisted_settings_without_authority() {
    let old = tempfile::tempdir().unwrap();
    std::fs::write(old.path().join("settings.json"), json!({
        "FilenameTemplate":"{source}/{id}.{ext}", "ConcurrentDownloads":4,
        "GalleryScale":1.5, "SaveMetadataJson":true,
        "DownloadDuplicatesPolicy":"skip", "LoadLastSessionEnabled":true,
        "DefaultDownloadDirectory":"https://not-a-directory.invalid/", "UnknownSecret":"never-copy-this"
    }).to_string()).unwrap();
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.settings().concurrent_downloads, Some(4));
    assert_eq!(p.settings().requested_session_restore, Some(true));
    assert_eq!(
        p.settings().directory_hint.as_deref(),
        Some("https://not-a-directory.invalid/")
    );
    assert!(!format!("{p:?}").contains("never-copy-this"));
    assert!(p.warnings().iter().any(|s| s.contains("vault")));
}

#[test]
fn repeated_import_never_duplicates_or_resurrects_deleted_rows() {
    let old = tempfile::tempdir().unwrap();
    let new = tempfile::tempdir().unwrap();
    settings(old.path());
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let p = legacy::preview(old.path()).unwrap();
    let run = || {
        legacy::import_into(
            p.select(ImportSelection {
                search_ids: vec![p.searches()[0].id.clone()],
                pool_ids: vec![],
            })
            .unwrap()
            .confirm(p.content_digest())
            .unwrap(),
            &path,
        )
        .unwrap()
    };
    assert_eq!(run().imported_searches, 1);
    assert_eq!(run().skipped_searches, 1);
    let mut store = StateStore::open(&path).unwrap();
    store.delete_search(&p.searches()[0].id).unwrap();
    assert_eq!(run().skipped_searches, 1);
    assert!(store.saved_searches().unwrap().is_empty());
}

#[test]
fn current_id_and_ascii_name_collisions_are_never_overwritten() {
    let old = tempfile::tempdir().unwrap();
    let new = tempfile::tempdir().unwrap();
    settings(old.path());
    let p = legacy::preview(old.path()).unwrap();
    for by_id in [true, false] {
        let path = new.path().join(format!("state-{by_id}.sqlite"));
        let mut store = StateStore::open(&path).unwrap();
        let existing = furchive_engine::storage::SavedSearch {
            id: if by_id {
                p.searches()[0].id.clone()
            } else {
                "current-id".into()
            },
            name: if by_id {
                "Current name".into()
            } else {
                "FOXES".into()
            },
            query: Default::default(),
        };
        store.save_search(&existing).unwrap();
        let confirmed = p
            .select(ImportSelection {
                search_ids: vec![p.searches()[0].id.clone()],
                pool_ids: vec![],
            })
            .unwrap()
            .confirm(p.content_digest())
            .unwrap();
        assert_eq!(
            legacy::import_into(confirmed, &path)
                .unwrap()
                .skipped_searches,
            1
        );
        assert_eq!(store.saved_searches().unwrap(), vec![existing]);
    }
}

#[test]
fn explicit_selected_search_import_is_persistent() {
    let old = tempfile::tempdir().unwrap();
    let new = tempfile::tempdir().unwrap();
    settings(old.path());
    let path = new.path().join("state.sqlite");
    drop(StateStore::open(&path).unwrap());
    let inventory = legacy::preview(old.path()).unwrap();
    assert_eq!(inventory.searches().len(), 1);
    assert!(
        StateStore::open(&path)
            .unwrap()
            .saved_searches()
            .unwrap()
            .is_empty()
    );
    let plan = inventory
        .select(ImportSelection {
            search_ids: vec![inventory.searches()[0].id.clone()],
            pool_ids: vec![],
        })
        .unwrap();
    let confirmed = plan.confirm(inventory.content_digest()).unwrap();
    let report = legacy::import_into(confirmed, &path).unwrap();
    assert_eq!(report.imported_searches, 1);
    assert_eq!(
        StateStore::open(&path).unwrap().saved_searches().unwrap()[0].name,
        "Foxes"
    );
    assert!(!format!("{inventory:?}").contains("synthetic-never-retain"));
    assert!(!format!("{inventory:?}").contains("synthetic-user"));
}
