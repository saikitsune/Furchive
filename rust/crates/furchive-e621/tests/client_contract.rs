use furchive_domain::SearchQuery;
#[test]
fn safe_search_page_uses_raw_upstream_count_for_pagination() {
    let raw = serde_json::json!({"posts":[{"id":1,"rating":"e"},{"id":2,"rating":"s"}]});
    let result = furchive_e621::parse_search_page(raw, &SearchQuery::default(), 1, 2).unwrap();
    assert_eq!(result.items.len(), 1);
    assert_eq!(result.items[0].key.id(), "2");
    assert_eq!(result.next_page, Some(2));
}
