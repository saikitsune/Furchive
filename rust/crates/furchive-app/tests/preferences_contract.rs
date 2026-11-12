use furchive_app::settings::{Preferences, Settings, SettingsStore};
#[test]
fn preference_store_acknowledges_only_persisted_validated_changes_and_preserves_directory() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let initial = Settings {
        download_directory: Some(dir.path().to_owned()),
        ..Default::default()
    };
    initial.save(&file).unwrap();
    let store = SettingsStore::open(&file).unwrap();
    let mut prefs = Preferences::from(store.current().unwrap());
    prefs.gallery_scale = 1.5;
    prefs.filename_template = "{source}/{id}.{ext}".into();
    store.apply(prefs.clone()).unwrap();
    assert_eq!(
        Settings::load(&file).unwrap().download_directory,
        Some(dir.path().to_owned())
    );
    assert_eq!(
        SettingsStore::open(&file)
            .unwrap()
            .current()
            .unwrap()
            .gallery_scale,
        1.5
    );
    let old = std::fs::read(&file).unwrap();
    prefs.filename_template = "../escape/{id}".into();
    assert!(store.apply(prefs).is_err());
    assert_eq!(std::fs::read(&file).unwrap(), old);
    assert_eq!(
        store.current().unwrap().filename_template,
        "{source}/{id}.{ext}"
    );
}
#[test]
fn preference_store_never_updates_memory_if_atomic_storage_fails() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let store = SettingsStore::open(&file).unwrap();
    let old = store.current().unwrap();
    std::fs::create_dir(&file).unwrap();
    let mut prefs = Preferences::from(old.clone());
    prefs.start_muted = false;
    assert!(store.apply(prefs).is_err());
    assert_eq!(store.current().unwrap(), old);
}
