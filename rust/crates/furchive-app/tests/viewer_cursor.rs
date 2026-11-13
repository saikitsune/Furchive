use furchive_app::viewer_cursor::{Intent, Turn, ViewerCursor};
use furchive_domain::{RatingFilter, SearchQuery};
#[test]
fn search_edges_keep_submitted_intent_and_refuse_stale_foreign_or_nonedge_selection() {
    let cursor = ViewerCursor::default();
    let q = SearchQuery::default();
    cursor
        .search(
            7,
            Intent::Search(q.clone()),
            2,
            Some(3),
            vec!["100".into(), "101".into()],
        )
        .unwrap();
    assert!(cursor.turn(6, "101", 1).is_err());
    assert!(cursor.turn(7, "foreign", 1).is_err());
    assert!(cursor.turn(7, "100", 1).is_err());
    assert!(cursor.turn(7, "101", 2).is_err());
    let turn = cursor.turn(7, "101", 1).unwrap().unwrap();
    assert_eq!(turn.page, 3);
    assert_eq!(turn.offset, 0);
    assert!(matches!(turn.intent,Intent::Search(query)if query==q));
    let turn = cursor.turn(7, "100", -1).unwrap().unwrap();
    assert_eq!(turn.page, 1);
    assert!(turn.previous);
    cursor
        .search(8, Intent::Search(q), 1, None, vec!["100".into()])
        .unwrap();
    assert!(cursor.turn(8, "100", -1).unwrap().is_none());
    assert!(cursor.turn(8, "100", 1).unwrap().is_none());
    assert!(cursor.turn(7, "101", 1).is_err());
}
#[test]
fn late_cursor_publication_cannot_replace_newer_query_generation() {
    let cursor = ViewerCursor::default();
    cursor
        .search(
            9,
            Intent::Search(Default::default()),
            1,
            Some(2),
            vec!["900".into()],
        )
        .unwrap();
    assert!(
        cursor
            .search(
                8,
                Intent::Search(Default::default()),
                1,
                Some(2),
                vec!["800".into()]
            )
            .is_err()
    );
    assert_eq!(cursor.turn(9, "900", 1).unwrap().unwrap().page, 2);
}
#[test]
fn pool_edges_use_authoritative_offsets_ratings_and_empty_pages_are_bounded() {
    let cursor = ViewerCursor::default();
    let ratings = RatingFilter::default();
    cursor
        .pool(4, 3, ratings.clone(), 50, 105, Some(100), vec!["90".into()])
        .unwrap();
    let next = cursor.turn(4, "90", 1).unwrap().unwrap();
    assert_eq!(next.offset, 100);
    assert!(matches!(next.intent,Intent::Pool{id:3,ratings:r}if r==ratings));
    let prev = cursor.turn(4, "90", -1).unwrap().unwrap();
    assert_eq!(prev.offset, 0);
    let next = Turn {
        intent: Intent::Search(SearchQuery::default()),
        page: 2,
        offset: 0,
        previous: false,
    };
    let next = next.follow_empty(Some(3), None).unwrap().unwrap();
    assert_eq!(next.page, 3);
    assert!(next.follow_empty(Some(3), None).is_err());
    let prev = Turn {
        intent: Intent::Search(SearchQuery::default()),
        page: 1,
        offset: 0,
        previous: true,
    };
    assert!(prev.follow_empty(None, None).unwrap().is_none());
    cursor.clear().unwrap();
    assert!(cursor.turn(4, "90", 1).is_err());
}
