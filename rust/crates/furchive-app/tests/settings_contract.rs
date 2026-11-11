use furchive_app::settings::{DuplicatePolicy, Settings};
#[test]
fn first_run_settings_are_safe_bounded_and_do_not_restore_remote_session() {
    let settings = Settings::default();
    assert_eq!(settings.concurrent_downloads, 3);
    assert_eq!(settings.duplicates, DuplicatePolicy::Skip);
    assert!(!settings.restore_session);
    assert!(settings.validate().is_ok());
    assert!(
        !serde_json::to_string(&settings)
            .unwrap()
            .contains("api_key")
    );
}
#[test]
fn invalid_settings_never_replace_last_good_settings() {
    let root = tempfile::tempdir().unwrap();
    let path = root.path().join("settings.json");
    let settings = Settings::default();
    settings.save(&path).unwrap();
    let before = std::fs::read(&path).unwrap();
    let mut invalid = settings.clone();
    invalid.filename_template = "../{id}.{ext}".into();
    assert!(invalid.save(&path).is_err());
    assert_eq!(before, std::fs::read(&path).unwrap());
    assert_eq!(Settings::load(&path).unwrap(), settings);
}
