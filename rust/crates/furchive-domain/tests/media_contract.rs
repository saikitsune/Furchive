use furchive_domain::{MediaItem, MediaKind, PostKey, Rating, TagCategory};

#[test]
fn contributor_and_unknown_categories_roundtrip_as_original_keys() {
    let mut item = MediaItem::new(PostKey::new("e621", "42").unwrap());
    item.tags = serde_json::from_str(r#"{"contributor":["helper"],"future_category":["unmodified"],"artist":["sound_warning","artist"]}"#).unwrap();
    item.kind = MediaKind::Unknown("future_codec".into());
    assert_eq!(item.tags[&TagCategory::Contributor], ["helper"]);
    assert_eq!(
        item.tags[&TagCategory::Unknown("future_category".into())],
        ["unmodified"]
    );
    for (id, category) in [
        (0, TagCategory::General),
        (1, TagCategory::Artist),
        (2, TagCategory::Contributor),
        (3, TagCategory::Copyright),
        (4, TagCategory::Character),
        (5, TagCategory::Species),
        (6, TagCategory::Invalid),
        (7, TagCategory::Meta),
        (8, TagCategory::Lore),
    ] {
        assert_eq!(TagCategory::from_e621_id(id), category);
    }
    assert_eq!(
        TagCategory::from_e621_id(99),
        TagCategory::Unknown("99".into())
    );
    let json = serde_json::to_value(&item).unwrap();
    assert_eq!(json["tags"]["artist"][0], "sound_warning");
    assert_eq!(serde_json::from_value::<MediaItem>(json).unwrap(), item);
}

#[test]
fn pool_order_and_identity_have_a_separate_typed_contract() {
    use furchive_domain::{Pool, PoolKey};
    let key = PoolKey::new("e621", "10").unwrap();
    let mut pool = Pool::new(key.clone(), "ordered".into());
    pool.post_ids = vec!["30".into(), "2".into(), "10".into()];
    pool.description = Some("pool text".into());
    assert_eq!(
        serde_json::from_str::<Pool>(&serde_json::to_string(&pool).unwrap()).unwrap(),
        pool
    );
    assert_eq!(key.source(), "e621");
    assert_eq!(key.id(), "10");
    assert!(PoolKey::new("", "10").is_err());
    assert!(serde_json::from_str::<PoolKey>(r#"{"source":"e621", "id":""}"#).is_err());
}

#[test]
fn media_identity_and_metadata_roundtrip_without_safe_defaults() {
    let key = PostKey::new("e621", "42").unwrap();
    let mut item = MediaItem::new(key.clone());
    item.description = "<b>upstream text, not HTML authority</b>".into();
    item.artists = vec!["artist".into()];
    assert_eq!(item.rating, Rating::Unknown(String::new()));
    assert!(item.full_url.is_none());
    let json = serde_json::to_string(&item).unwrap();
    assert_eq!(serde_json::from_str::<MediaItem>(&json).unwrap(), item);
    assert_ne!(key, PostKey::new("other", "42").unwrap());
    assert_eq!(key.source(), "e621");
    assert_eq!(key.id(), "42");
    for (source, id) in [("", "42"), ("e621", ""), ("e621", "bad\0id")] {
        assert!(PostKey::new(source, id).is_err());
    }
    assert!(serde_json::from_str::<PostKey>(r#"{"source":"", "id":"42"}"#).is_err());
}
