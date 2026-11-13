use furchive_app::settings::preview_templates;

#[test]
fn preview_renders_both_unsaved_templates_with_portable_sample_values() {
    let result = preview_templates(
        "{source}/{artist}/{id}_{safeTitle}.{ext}",
        "{source}/pools/{pool_name}/{page_number}_{id}.{ext}",
    )
    .unwrap();
    assert_eq!(
        result.filename,
        "e621/sample_artist/12345_Sample_artwork.png"
    );
    assert_eq!(result.pool_filename, "e621/pools/Sample_pool/7_12345.png");
    let encoded = serde_json::to_value(&result).unwrap();
    assert_eq!(encoded.as_object().unwrap().len(), 2);
}

#[test]
fn preview_rejects_bad_tokens_absolute_parent_and_overlong_templates() {
    for bad in [
        "",
        "../escape",
        "/absolute",
        "C:/drive",
        "\\\\host\\share",
        "{unknown}",
        "{id",
        "tail.",
    ] {
        assert!(preview_templates(bad, "{id}.{ext}").is_err(), "{bad}");
        assert!(preview_templates("{id}.{ext}", bad).is_err(), "{bad}");
    }
    assert!(preview_templates(&"x".repeat(1025), "{id}").is_err());
    assert!(preview_templates(&"x".repeat(256), "{id}").is_err());
    let preview = preview_templates(
        "CON/{safeTitle}.{ext}",
        "{pool_name}/{page_number}_{id}.{ext}",
    )
    .unwrap();
    assert_eq!(preview.filename, "_CON/Sample_artwork.png");
}

#[test]
fn preview_does_not_mutate_a_settings_store_or_create_downloads() {
    use furchive_app::settings::{Settings, SettingsStore};
    let directory = tempfile::tempdir().unwrap();
    let file = directory.path().join("settings.json");
    Settings::default().save(&file).unwrap();
    let store = SettingsStore::open(&file).unwrap();
    let before = std::fs::read(&file).unwrap();
    let value = store.current().unwrap();
    preview_templates("unsaved/{id}.{ext}", "pool/{page_number}.{ext}").unwrap();
    assert_eq!(store.current().unwrap(), value);
    assert_eq!(std::fs::read(&file).unwrap(), before);
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 1);
}
