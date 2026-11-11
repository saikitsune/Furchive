#[test]
fn corrupt_legacy_chip_types_are_reported_not_silently_removed() {
    let root = tempfile::tempdir().unwrap();
    let searches = serde_json::json!([{"Name":"Broken","IncludeTags":["fox",99],"ExcludeTags":[],"RatingFilterIndex":3}]);
    std::fs::write(
        root.path().join("settings.json"),
        serde_json::to_vec(&serde_json::json!({"SavedSearches":searches.to_string()})).unwrap(),
    )
    .unwrap();
    let preview = furchive_app::legacy::read_settings(root.path()).unwrap();
    assert!(preview.searches.is_empty());
    assert_eq!(preview.warnings.len(), 1);
}
