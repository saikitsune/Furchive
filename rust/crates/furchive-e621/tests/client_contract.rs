use furchive_domain::SearchQuery;
#[test]
fn upstream_md5_is_retained_validated_and_legacy_cached_records_remain_readable() {
    let raw =
        serde_json::json!({"id":1,"rating":"s","file":{"md5":"abcdef0123456789abcdef0123456789"}});
    let p = furchive_e621::map_post(&raw).unwrap();
    assert_eq!(
        p.file_md5.as_deref(),
        Some("abcdef0123456789abcdef0123456789")
    );
    let mut old = serde_json::to_value(p).unwrap();
    old.as_object_mut().unwrap().remove("file_md5");
    let post: furchive_domain::MediaItem = serde_json::from_value(old).unwrap();
    assert!(post.file_md5.is_none());
    for hash in ["bad", "ABCDEF0123456789ABCDEF0123456789", "../../escape"] {
        let raw = serde_json::json!({"id":1,"file":{"md5":hash}});
        assert!(furchive_e621::map_post(&raw).is_err());
    }
}
#[test]
fn safe_search_page_uses_raw_upstream_count_for_pagination() {
    let raw = serde_json::json!({"posts":[{"id":1,"rating":"e"},{"id":2,"rating":"s"}]});
    let result = furchive_e621::parse_search_page(raw, &SearchQuery::default(), 1, 2).unwrap();
    assert_eq!(result.items.len(), 1);
    assert_eq!(result.items[0].key.id(), "2");
    assert_eq!(result.next_page, Some(2));
}
