#[test]
fn pool_metadata_preserves_authoritative_order_and_missing_members() {
    let value = serde_json::json!({"id":7,"name":"a_pool","description":"pool description","post_ids":[30,2,10],"is_active":true,"category":"series"});
    let pool = furchive_e621::map_pool(&value).unwrap();
    assert_eq!(pool.key.id(), "7");
    assert_eq!(pool.post_ids, vec!["30", "2", "10"]);
    assert_eq!(pool.description.as_deref(), Some("pool description"));
}
#[test]
fn suggestions_preserve_current_numeric_contributor_and_unknown_categories() {
    let values = serde_json::json!([{"name":"voice_actor","category":2,"post_count":45},{"name":"future","category":99,"post_count":3}]);
    let tags = furchive_e621::map_suggestions(values).unwrap();
    assert_eq!(tags[0].category, furchive_domain::TagCategory::Contributor);
    assert_eq!(
        tags[1].category,
        furchive_domain::TagCategory::Unknown("99".into())
    );
}
