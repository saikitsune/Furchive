use furchive_app::{
    browsing_session::{BrowsingSession, SessionQuery},
    settings::{Preferences, Settings, SettingsStore},
};
use furchive_domain::SearchQuery;

#[test]
fn remembered_session_is_opt_in_persisted_and_not_credential_or_media_authority() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let store = SettingsStore::open(&file).unwrap();
    let session = BrowsingSession::Search {
        query: SessionQuery::from(SearchQuery::default()),
        page: 2,
    };
    assert!(store.session_ticket(1).unwrap().is_none());
    assert!(store.browsing_session().unwrap().is_none());
    let mut prefs = Preferences::from(store.current().unwrap());
    prefs.restore_session = true;
    store.apply(prefs).unwrap();
    let ticket = store.session_ticket(2).unwrap().unwrap();
    store.remember_session(ticket, session.clone()).unwrap();
    let fresh = SettingsStore::open(&file).unwrap();
    assert_eq!(fresh.browsing_session().unwrap(), Some(session));
    let data = std::fs::read_to_string(&file).unwrap();
    for secret_field in [
        "api_key",
        "password",
        "full_url",
        "download_grant",
        "selected_id",
    ] {
        assert!(!data.contains(secret_field));
    }
    let mut prefs = Preferences::from(fresh.current().unwrap());
    prefs.restore_session = false;
    fresh.apply(prefs).unwrap();
    assert!(fresh.browsing_session().unwrap().is_none());
    assert!(Settings::load(&file).unwrap().last_session.is_none());
}

#[test]
fn older_requests_and_post_forget_or_preference_change_completions_cannot_restore_history() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let store = SettingsStore::open(&file).unwrap();
    let mut prefs = Preferences::from(store.current().unwrap());
    prefs.restore_session = true;
    store.apply(prefs.clone()).unwrap();
    let session = BrowsingSession::Search {
        query: SearchQuery::default().into(),
        page: 1,
    };
    let old = store.session_ticket(1).unwrap().unwrap();
    let latest = store.session_ticket(2).unwrap().unwrap();
    assert!(store.remember_session(old, session.clone()).is_err());
    store.remember_session(latest, session.clone()).unwrap();
    let pending = store.session_ticket(3).unwrap().unwrap();
    store.forget_browsing_session().unwrap();
    assert!(store.remember_session(pending, session.clone()).is_err());
    assert!(store.browsing_session().unwrap().is_none());
    let pending = store.session_ticket(4).unwrap().unwrap();
    prefs.restore_session = false;
    store.apply(prefs.clone()).unwrap();
    prefs.restore_session = true;
    store.apply(prefs).unwrap();
    assert!(store.remember_session(pending, session).is_err());
    assert!(store.browsing_session().unwrap().is_none());
}

#[test]
fn session_persistence_failures_leave_previous_memory_and_file_intact() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("settings.json");
    let store = SettingsStore::open(&file).unwrap();
    let mut prefs = Preferences::from(store.current().unwrap());
    prefs.restore_session = true;
    store.apply(prefs).unwrap();
    let session = BrowsingSession::Search {
        query: SearchQuery::default().into(),
        page: 1,
    };
    let ticket = store.session_ticket(1).unwrap().unwrap();
    store.remember_session(ticket, session.clone()).unwrap();
    std::fs::rename(&file, dir.path().join("preserved.json")).unwrap();
    std::fs::create_dir(&file).unwrap();
    assert!(store.forget_browsing_session().is_err());
    assert_eq!(store.browsing_session().unwrap(), Some(session.clone()));
    let ticket = store.session_ticket(2).unwrap().unwrap();
    assert!(
        store
            .remember_session(
                ticket,
                BrowsingSession::Search {
                    query: SearchQuery::default().into(),
                    page: 3
                }
            )
            .is_err()
    );
    assert_eq!(store.browsing_session().unwrap(), Some(session));
}

#[test]
fn persisted_session_rejects_unknown_fields_invalid_ratings_and_unbounded_positions() {
    use furchive_app::browsing_session::BrowsingSession;
    for malformed in [
        serde_json::json!({"mode":"search","query":{"include":[],"exclude":[],"ratings":{"only":["safe"]},"artist":null,"sort":"newest","api_key":"not-admitted"},"page":1}),
        serde_json::json!({"mode":"search","query":{"include":[],"exclude":[],"ratings":{"only":["safe"]},"artist":null,"sort":"newest"},"page":1,"selected_id":"42"}),
        serde_json::json!({"mode":"pool","id":7,"ratings":"any","offset":0,"download_directory":"/escape"}),
    ] {
        assert!(serde_json::from_value::<BrowsingSession>(malformed).is_err());
    }
    for bad in [
        BrowsingSession::Search {
            query: SearchQuery::default().into(),
            page: 0,
        },
        BrowsingSession::Search {
            query: SearchQuery::default().into(),
            page: 100001,
        },
        BrowsingSession::Favorites {
            query: SearchQuery::default().into(),
            username: "user rating:e".into(),
            page: 1,
        },
        BrowsingSession::Pool {
            id: 0,
            ratings: furchive_domain::RatingFilter::Any,
            offset: 0,
        },
        BrowsingSession::Pool {
            id: 7,
            ratings: furchive_domain::RatingFilter::Any,
            offset: 1,
        },
        BrowsingSession::Pool {
            id: 7,
            ratings: furchive_domain::RatingFilter::Only(vec![]),
            offset: 0,
        },
        BrowsingSession::Search {
            query: SearchQuery {
                include: vec!["x".repeat(257)],
                ..Default::default()
            }
            .into(),
            page: 1,
        },
    ] {
        assert!(bad.validate().is_err());
    }
    for good in [
        BrowsingSession::Favorites {
            query: SearchQuery::default().into(),
            username: "fixture_user".into(),
            page: 2,
        },
        BrowsingSession::Pool {
            id: 7,
            ratings: Default::default(),
            offset: 50,
        },
    ] {
        good.validate().unwrap();
    }
}

#[test]
fn cursor_snapshot_records_host_submitted_position_but_never_selected_media() {
    use furchive_app::viewer_cursor::{Intent, ViewerCursor};
    let cursor = ViewerCursor::default();
    let query = SearchQuery::default();
    cursor
        .search(
            1,
            Intent::Search(query.clone()),
            2,
            Some(3),
            vec!["42".into()],
        )
        .unwrap();
    assert_eq!(
        cursor.browsing_session(1).unwrap(),
        BrowsingSession::Search {
            query: query.into(),
            page: 2
        }
    );
    assert!(cursor.browsing_session(0).is_err());
    cursor.clear().unwrap();
    assert!(cursor.browsing_session(1).is_err());
}

#[test]
fn authority_boundary_invalidates_capture_without_loading_or_erasing_saved_controls() {
    let dir = tempfile::tempdir().unwrap();
    let store = SettingsStore::open(&dir.path().join("settings.json")).unwrap();
    let mut p = Preferences::from(store.current().unwrap());
    p.restore_session = true;
    store.apply(p).unwrap();
    let session = BrowsingSession::Search {
        query: SearchQuery::default().into(),
        page: 1,
    };
    let t = store.session_ticket(1).unwrap().unwrap();
    store.remember_session(t, session.clone()).unwrap();
    let pending = store.session_ticket(2).unwrap().unwrap();
    store.invalidate_session_capture().unwrap();
    assert!(store.remember_session(pending, session.clone()).is_err());
    assert_eq!(store.browsing_session().unwrap(), Some(session));
}
