use furchive_domain::{PoolKey, RatingFilter, SearchQuery};
use furchive_engine::storage::{SavedSearch, StateStore};

#[test]
fn typed_searches_update_delete_and_reject_invalid_queries() {
    let mut db = StateStore::in_memory().unwrap();
    let mut search = SavedSearch {
        id: "stable-id".into(),
        name: "Otters".into(),
        query: SearchQuery {
            include: vec!["otter".into()],
            ..Default::default()
        },
    };
    db.save_search(&search).unwrap();
    search.name = "Renamed".into();
    db.save_search(&search).unwrap();
    assert_eq!(db.saved_searches().unwrap(), [search.clone()]);
    search.query.ratings = RatingFilter::Only(vec![]);
    assert!(db.save_search(&search).is_err());
    assert_eq!(db.saved_searches().unwrap()[0].name, "Renamed");
    assert!(db
        .save_search(&SavedSearch {
            id: "".into(),
            name: "bad".into(),
            query: SearchQuery::default()
        })
        .is_err());
    db.delete_search("stable-id").unwrap();
    db.delete_search("stable-id").unwrap();
    assert!(db.saved_searches().unwrap().is_empty());
}

#[test]
fn pins_are_idempotent_and_source_scoped_without_cached_pools() {
    let mut db = StateStore::in_memory().unwrap();
    let a = PoolKey::new("e621", "1").unwrap();
    let b = PoolKey::new("other", "1").unwrap();
    db.set_pin(&a, true).unwrap();
    db.set_pin(&a, true).unwrap();
    db.set_pin(&b, true).unwrap();
    assert_eq!(db.pinned_pools().unwrap(), [a.clone(), b.clone()]);
    db.set_pin(&a, false).unwrap();
    assert_eq!(db.pinned_pools().unwrap(), [b]);
}
