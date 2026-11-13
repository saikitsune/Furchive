#![cfg(feature = "cache-test-contract")]
use furchive_app::media_cache::{MediaCache, Variant};
use furchive_domain::{MediaItem, PostKey};
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p
}
#[test]
fn shrinking_budget_evicts_old_handles_and_inflight_oversize_cannot_republish() {
    let cache = MediaCache::new(20).unwrap();
    cache.authorize(&[post("1"), post("2"), post("3")]).unwrap();
    let old = cache.grant_for_test("1", Variant::Full).unwrap();
    let stale = cache.grant_for_test("3", Variant::Full).unwrap();
    let first = cache
        .publish_for_test(old, vec![1; 8], "image/png")
        .unwrap();
    let second = cache
        .publish_for_test(
            cache.grant_for_test("2", Variant::Full).unwrap(),
            vec![2; 8],
            "image/png",
        )
        .unwrap();
    cache.set_budget(10).unwrap();
    assert_eq!(cache.resident_bytes().unwrap(), 8);
    assert!(cache.get(&first).is_none());
    assert!(cache.get(&second).is_some());
    assert!(
        cache
            .publish_for_test(stale, vec![3; 11], "image/png")
            .is_err()
    );
    assert_eq!(cache.resident_bytes().unwrap(), 8);
    assert!(cache.set_budget(0).is_err());
    assert!(cache.set_budget(512 * 1024 * 1024 + 1).is_err());
    assert_eq!(cache.resident_bytes().unwrap(), 8);
    cache.set_budget(20).unwrap();
    assert!(
        cache.get(&first).is_none(),
        "growing does not resurrect evicted handles"
    );
}
#[test]
fn preference_cache_apply_waits_for_validated_persistence_and_enforces_resident_budget() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let store = furchive_app::settings::SettingsStore::open(&file).unwrap();
    let cache = MediaCache::new(64 * 1024 * 1024).unwrap();
    cache.authorize(&[post("1"), post("2")]).unwrap();
    let first = cache
        .publish_for_test(
            cache.grant_for_test("1", Variant::Full).unwrap(),
            vec![1; 10 * 1024 * 1024],
            "image/png",
        )
        .unwrap();
    let second = cache
        .publish_for_test(
            cache.grant_for_test("2", Variant::Full).unwrap(),
            vec![2; 10 * 1024 * 1024],
            "image/png",
        )
        .unwrap();
    let mut prefs = furchive_app::settings::Preferences::from(store.current().unwrap());
    prefs.cache_limit_bytes = 16 * 1024 * 1024;
    store.apply_with_cache(prefs.clone(), &cache).unwrap();
    assert!(cache.get(&first).is_none());
    assert!(cache.get(&second).is_some());
    assert_eq!(cache.budget_bytes().unwrap(), 16 * 1024 * 1024);
    assert_eq!(
        furchive_app::settings::Settings::load(&file)
            .unwrap()
            .cache_limit_bytes,
        16 * 1024 * 1024
    );
    std::fs::remove_file(&file).unwrap();
    std::fs::create_dir(&file).unwrap();
    prefs.cache_limit_bytes = 32 * 1024 * 1024;
    assert!(store.apply_with_cache(prefs, &cache).is_err());
    assert_eq!(cache.budget_bytes().unwrap(), 16 * 1024 * 1024);
    assert_eq!(store.current().unwrap().cache_limit_bytes, 16 * 1024 * 1024);
}
#[test]
fn configured_large_legacy_cache_budget_has_explicit_resident_cap() {
    let mut settings = furchive_app::settings::Settings {
        cache_limit_bytes: 16 * 1024 * 1024 * 1024,
        ..Default::default()
    };
    assert_eq!(settings.resident_media_budget(), 512 * 1024 * 1024);
    settings.cache_limit_bytes = 16 * 1024 * 1024;
    assert_eq!(settings.resident_media_budget(), 16 * 1024 * 1024);
}
