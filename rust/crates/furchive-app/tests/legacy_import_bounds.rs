use furchive_app::legacy::{self, ImportSelection};
use serde_json::json;
use std::path::Path;

fn write(root: &Path, value: serde_json::Value) {
    std::fs::write(root.join("settings.json"), value.to_string()).unwrap();
}
#[test]
fn ambiguous_duplicate_json_keys_fail_closed() {
    let old = tempfile::tempdir().unwrap();
    for input in [
        r#"{"SavedSearches":"[]","SavedSearches":"[]"}"#,
        r#"{"SavedSearches":"[{\"Name\":\"one\",\"Name\":\"two\"}]"}"#,
    ] {
        std::fs::write(old.path().join("settings.json"), input).unwrap();
        assert!(legacy::preview(old.path()).is_err());
    }
}

#[test]
fn duplicate_legacy_searches_are_deduplicated_with_a_warning() {
    let old = tempfile::tempdir().unwrap();
    let search = json!({"Name":"one","IncludeTags":["otter"]});
    write(
        old.path(),
        json!({"SavedSearches":json!([search,search]).to_string()}),
    );
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.searches().len(), 1);
    assert!(p.warnings().iter().any(|w| w.contains("duplicate")));
}

#[test]
fn invalid_individual_fields_warn_without_coercing_ratings_or_types() {
    let old = tempfile::tempdir().unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([
        {"Name":"good","IncludeTags":["otter"],"RatingFilterIndex":0},
        {"Name":"bad-rating","RatingFilterIndex":99},
        {"Name":"bad-type","RatingFilterIndex":"3"},
        {"Name":"bad-chips","IncludeTags":[true]},
        {"Name":true}, null
    ]).to_string(),"ConcurrentDownloads":99,"GalleryScale":0.1,"SaveMetadataJson":"false","FilenameTemplate":"../{id}"}),
    );
    let p = legacy::preview(old.path()).unwrap();
    assert_eq!(p.searches().len(), 1);
    assert_eq!(
        p.warnings()
            .iter()
            .filter(|w| w.contains("rejected"))
            .count(),
        9
    );
    assert_eq!(p.settings().concurrent_downloads, None);
    assert_eq!(p.settings().gallery_scale, None);
    assert_eq!(p.settings().save_metadata, None);
    assert_eq!(p.settings().filename_template, None);
}
#[test]
fn malformed_containers_depth_and_record_limits_fail_closed() {
    let old = tempfile::tempdir().unwrap();
    for value in [
        json!([]),
        json!(null),
        json!({"SavedSearches":42}),
        json!({"SavedSearches":"{}"}),
        json!({"SavedSearches":"null"}),
        json!({"SavedSearches":"["}),
    ] {
        write(old.path(), value);
        assert!(legacy::preview(old.path()).is_err());
    }
    let deep = format!("{{\"ignored\":{}0{}}}", "[".repeat(33), "]".repeat(33));
    std::fs::write(old.path().join("settings.json"), deep).unwrap();
    assert!(legacy::preview(old.path()).is_err());
    write(
        old.path(),
        json!({"SavedSearches":vec![json!({"Name":"search"});1001].into_iter().collect::<serde_json::Value>().to_string()}),
    );
    assert!(legacy::preview(old.path()).is_err());
}
#[test]
fn json_depth_budget_ignores_brackets_inside_escaped_strings() {
    let old = tempfile::tempdir().unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([{"Name":"brackets [\\\"ok]","IncludeTags":["otter"]}]).to_string(),"Ignored":"[".repeat(100)}),
    );
    assert_eq!(legacy::preview(old.path()).unwrap().searches().len(), 1);
}

#[test]
fn actual_settings_bytes_are_capped() {
    let old = tempfile::tempdir().unwrap();
    std::fs::write(
        old.path().join("settings.json"),
        vec![b' '; legacy::MAX_SETTINGS_BYTES + 1],
    )
    .unwrap();
    assert!(legacy::read_settings(old.path()).is_err());
    assert!(legacy::preview(old.path()).is_err());
}
#[test]
fn secret_changes_do_not_affect_digest_or_item_identity() {
    let old = tempfile::tempdir().unwrap();
    let searches = json!([{"Name":"one","IncludeTags":["otter"]}]).to_string();
    write(
        old.path(),
        json!({"SavedSearches":searches,"E621ApiKey":"fixture-secret-a","OtherToken":{"deep":"fixture-secret-b"}}),
    );
    let a = legacy::preview(old.path()).unwrap();
    write(
        old.path(),
        json!({"SavedSearches":searches,"E621ApiKey":"fixture-secret-c","OtherToken":["fixture-secret-d"]}),
    );
    let b = legacy::preview(old.path()).unwrap();
    assert_eq!(a.content_digest(), b.content_digest());
    assert_eq!(a.searches()[0].id, b.searches()[0].id);
    for p in [&a, &b] {
        assert!(!format!("{p:?}").contains("fixture-secret-"));
    }
}
#[test]
fn exact_user_selected_subset_does_not_import_other_records() {
    let old = tempfile::tempdir().unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([{"Name":"one"},{"Name":"two"}]).to_string()}),
    );
    let p = legacy::preview(old.path()).unwrap();
    let new = tempfile::tempdir().unwrap();
    let state = new.path().join("state.sqlite");
    drop(furchive_engine::storage::StateStore::open(&state).unwrap());
    let selected = p
        .select(ImportSelection {
            search_ids: vec![p.searches()[1].id.clone()],
            pool_ids: vec![],
        })
        .unwrap()
        .confirm(p.content_digest())
        .unwrap();
    assert_eq!(
        legacy::import_into(selected, &state)
            .unwrap()
            .imported_searches,
        1
    );
    let searches = furchive_engine::storage::StateStore::open(&state)
        .unwrap()
        .saved_searches()
        .unwrap();
    assert_eq!(searches.len(), 1);
    assert_eq!(searches[0].name, "two");
}

#[test]
fn selection_and_confirmation_are_separate_strict_steps() {
    let old = tempfile::tempdir().unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([{"Name":"one"},{"Name":"two"}]).to_string()}),
    );
    let p = legacy::preview(old.path()).unwrap();
    assert!(p.select(ImportSelection::default()).is_err());
    assert!(
        p.select(ImportSelection {
            search_ids: vec!["unknown".into()],
            pool_ids: vec![]
        })
        .is_err()
    );
    let id = p.searches()[0].id.clone();
    assert!(
        p.select(ImportSelection {
            search_ids: vec![id.clone(), id.clone()],
            pool_ids: vec![]
        })
        .is_err()
    );
    assert!(
        p.select(ImportSelection {
            search_ids: vec![id],
            pool_ids: vec![]
        })
        .unwrap()
        .confirm("wrong digest")
        .is_err()
    );
}
#[test]
fn confirmed_inventory_is_not_replaced_by_changed_source() {
    let old = tempfile::tempdir().unwrap();
    let new = tempfile::tempdir().unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([{"Name":"reviewed"}]).to_string()}),
    );
    let p = legacy::preview(old.path()).unwrap();
    let confirmation = p
        .select(ImportSelection {
            search_ids: vec![p.searches()[0].id.clone()],
            pool_ids: vec![],
        })
        .unwrap()
        .confirm(p.content_digest())
        .unwrap();
    write(
        old.path(),
        json!({"SavedSearches":json!([{"Name":"unreviewed"}]).to_string()}),
    );
    let state = new.path().join("state.sqlite");
    drop(furchive_engine::storage::StateStore::open(&state).unwrap());
    legacy::import_into(confirmation, &state).unwrap();
    let searches = furchive_engine::storage::StateStore::open(&state)
        .unwrap()
        .saved_searches()
        .unwrap();
    assert_eq!(searches.len(), 1);
    assert_eq!(searches[0].name, "reviewed");
}
