use furchive_domain::{Rating, RatingFilter, SearchQuery, SortOrder};

#[test]
fn rating_selections_are_not_anded_and_unknown_is_never_safe() {
    let mut query = SearchQuery {
        ratings: RatingFilter::Only(vec![Rating::Safe, Rating::Questionable, Rating::Safe]),
        ..Default::default()
    };
    assert_eq!(query.e621_tags().unwrap(), "order:id_desc");
    assert!(query.ratings.accepts(&Rating::Questionable));
    assert!(!query.ratings.accepts(&Rating::Explicit));
    assert!(!RatingFilter::Any.accepts(&Rating::Unknown("future".into())));
    assert!(!RatingFilter::Any.accepts(&Rating::Restricted));
    query.ratings = RatingFilter::Only(vec![Rating::Explicit, Rating::Explicit]);
    assert_eq!(query.e621_tags().unwrap(), "rating:e order:id_desc");
    query.ratings = RatingFilter::Only(vec![]);
    assert!(query.e621_tags().is_err());
    query.ratings = RatingFilter::Only(vec![Rating::Unknown("future".into())]);
    assert!(query.e621_tags().is_err());
}

#[test]
fn sort_and_artist_are_explicit_and_query_roundtrips() {
    for (sort, tag) in [
        (SortOrder::Newest, "id_desc"),
        (SortOrder::Oldest, "id_asc"),
        (SortOrder::Score, "score"),
        (SortOrder::Favorites, "favcount"),
        (SortOrder::Random, "random"),
    ] {
        let query = SearchQuery {
            sort,
            artist: Some("river_otter".into()),
            ratings: RatingFilter::Any,
            ..SearchQuery::default()
        };
        assert_eq!(
            query.e621_tags().unwrap(),
            format!("artist:river_otter order:{tag}")
        );
        assert_eq!(
            serde_json::from_str::<SearchQuery>(&serde_json::to_string(&query).unwrap()).unwrap(),
            query
        );
    }
    assert!(SearchQuery {
        artist: Some("a rating:e".into()),
        ..SearchQuery::default()
    }
    .e621_tags()
    .is_err());
}

#[test]
fn include_exclude_aliases_become_individual_api_terms() {
    let query = SearchQuery {
        include: vec!["otter".into(), "No_Artist".into()],
        exclude: vec!["sound_warning".into(), "no_artist".into()],
        ..SearchQuery::default()
    };
    assert_eq!(
        query.e621_tags().unwrap(),
        "otter arttags:0 -sound_warning -arttags:0 rating:s order:id_desc"
    );
}

#[test]
fn rejects_chip_injection_but_preserves_upstream_metatags() {
    for invalid in ["", " ", "two tags", "a\nb", "-otter", "\0", "~otter"] {
        let query = SearchQuery {
            include: vec![invalid.into()],
            ..SearchQuery::default()
        };
        assert!(query.e621_tags().is_err(), "accepted {invalid:?}");
    }
    let query = SearchQuery {
        include: vec!["fav:someone".into(), "artist:name".into()],
        ..SearchQuery::default()
    };
    assert_eq!(
        query.e621_tags().unwrap(),
        "fav:someone artist:name rating:s order:id_desc"
    );
}
