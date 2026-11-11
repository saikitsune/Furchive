#[test]
fn malformed_legacy_outer_shape_is_never_a_successful_empty_import() {
    let root = tempfile::tempdir().unwrap();
    for value in [
        serde_json::json!([]),
        serde_json::json!(null),
        serde_json::json!({"SavedSearches":42}),
        serde_json::json!({"SavedSearches":[]}),
    ] {
        std::fs::write(
            root.path().join("settings.json"),
            serde_json::to_vec(&value).unwrap(),
        )
        .unwrap();
        assert!(furchive_app::legacy::read_settings(root.path()).is_err());
    }
}
#[test]
fn oversized_serialized_settings_never_replace_good_bytes() {
    use furchive_app::settings::Settings;
    let root = tempfile::tempdir().unwrap();
    let path = root.path().join("settings.json");
    let mut settings = Settings::default();
    settings.save(&path).unwrap();
    let before = std::fs::read(&path).unwrap();
    settings.download_directory = Some(
        std::env::current_dir()
            .unwrap()
            .join("x".repeat(1024 * 1024)),
    );
    assert!(settings.save(&path).is_err());
    assert_eq!(before, std::fs::read(&path).unwrap());
}
