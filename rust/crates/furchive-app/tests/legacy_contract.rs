#[test]
fn legacy_saved_searches_are_read_without_importing_api_keys() {
    let dir = tempfile::tempdir().unwrap();
    let content = serde_json::json!({"E621ApiKey":"private fixture value","SavedSearches":"[{\"Name\":\"Foxes\",\"IncludeTags\":[\"fox\"],\"ExcludeTags\":[\"comic\"],\"RatingFilterIndex\":3}]"});
    std::fs::write(
        dir.path().join("settings.json"),
        serde_json::to_vec(&content).unwrap(),
    )
    .unwrap();
    let before = std::fs::read(dir.path().join("settings.json")).unwrap();
    let data = furchive_app::legacy::read_settings(dir.path()).unwrap();
    assert_eq!(data.searches.len(), 1);
    assert_eq!(data.searches[0].name, "Foxes");
    assert!(!format!("{data:?}").contains("private fixture value"));
    assert_eq!(
        before,
        std::fs::read(dir.path().join("settings.json")).unwrap()
    );
}
