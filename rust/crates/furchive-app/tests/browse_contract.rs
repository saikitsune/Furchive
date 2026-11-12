//! Harmless local metadata contracts; no user account or network is consulted.
use furchive_app::browse::{assemble_pool_page, favorites_query};
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, Rating, RatingFilter, SearchQuery};
fn post(id: &str, rating: Rating) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.rating = rating;
    p
}
fn pool() -> Pool {
    let mut p = Pool::new(PoolKey::new("e621", "3").unwrap(), "Fixture pool".into());
    p.post_ids = vec!["10".into(), "2".into(), "30".into(), "9".into()];
    p
}
#[test]
fn pool_page_uses_authoritative_positions_and_never_compacts_filtered_or_missing_members() {
    let result = assemble_pool_page(
        pool(),
        vec![
            post("30", Rating::Safe),
            post("10", Rating::Safe),
            post("2", Rating::Questionable),
        ],
        0,
        4,
        &RatingFilter::default(),
    )
    .unwrap();
    assert_eq!(
        result
            .entries
            .iter()
            .map(|e| e.id.as_str())
            .collect::<Vec<_>>(),
        vec!["10", "2", "30", "9"]
    );
    assert_eq!(
        result
            .entries
            .iter()
            .map(|e| e.position)
            .collect::<Vec<_>>(),
        vec![0, 1, 2, 3]
    );
    assert_eq!(
        result
            .entries
            .iter()
            .map(|e| e.post.as_ref().map(|p| p.key.id()))
            .collect::<Vec<_>>(),
        vec![Some("10"), None, Some("30"), None]
    );
    assert_eq!(result.total, 4);
    assert_eq!(result.next_offset, None);
}
#[test]
fn pool_page_refuses_unrelated_duplicate_noncanonical_members_and_preserves_real_offsets() {
    let page = assemble_pool_page(
        pool(),
        vec![post("9", Rating::Safe)],
        2,
        2,
        &RatingFilter::Any,
    )
    .unwrap();
    assert_eq!(
        page.entries.iter().map(|e| e.position).collect::<Vec<_>>(),
        vec![2, 3]
    );
    assert!(
        assemble_pool_page(
            pool(),
            vec![post("2", Rating::Safe)],
            2,
            2,
            &RatingFilter::Any
        )
        .is_err()
    );
    assert!(
        assemble_pool_page(
            pool(),
            vec![post("9", Rating::Safe), post("9", Rating::Safe)],
            2,
            2,
            &RatingFilter::Any
        )
        .is_err()
    );
    let mut duplicate = pool();
    duplicate.post_ids[3] = "10".into();
    assert!(assemble_pool_page(duplicate, vec![], 0, 2, &RatingFilter::Any).is_err());
    let mut invalid = pool();
    invalid.post_ids[3] = "01".into();
    assert!(assemble_pool_page(invalid, vec![], 0, 2, &RatingFilter::Any).is_err());
    for (offset, limit) in [(0, 0), (0, 101), (5, 2), (usize::MAX, 2)] {
        assert!(assemble_pool_page(pool(), vec![], offset, limit, &RatingFilter::Any).is_err());
    }
    let page = assemble_pool_page(
        pool(),
        vec![post("2", Rating::Safe)],
        0,
        2,
        &RatingFilter::Any,
    )
    .unwrap();
    assert_eq!(page.next_offset, Some(2));
}
#[test]
fn favorites_are_an_explicit_validated_username_constraint_and_preserve_user_query() {
    let original = SearchQuery {
        include: vec!["fox".into()],
        exclude: vec!["comic".into()],
        ..Default::default()
    };
    let q = favorites_query(&original, "fixture_user").unwrap();
    assert_eq!(q.include, vec!["fox", "fav:fixture_user"]);
    assert_eq!(q.exclude, original.exclude);
    assert_eq!(q.ratings, original.ratings);
    assert_eq!(original.include, vec!["fox"]);
    for bad in ["", "someone rating:e", "-name", "name\n", "name:other"] {
        assert!(favorites_query(&original, bad).is_err());
    }
}
