use furchive_app::legacy_session::LegacySession;
use furchive_engine::storage::StateStore;
#[test]
fn only_confirmed_host_selected_snapshot_can_import_and_source_bytes_remain_untouched() {
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("legacy");
    std::fs::create_dir(&source).unwrap();
    let file = source.join("settings.json");
    let data = serde_json::json!({"SavedSearches":serde_json::to_string(&serde_json::json!([{"Name":"Recovered foxes","IncludeTags":["fox"],"ExcludeTags":[],"RatingFilter":"Safe"}])).unwrap(),"E621ApiKey":"synthetic-secret-excluded"});
    std::fs::write(&file, serde_json::to_vec(&data).unwrap()).unwrap();
    let original = std::fs::read(&file).unwrap();
    let db = dir.path().join("state.sqlite");
    drop(StateStore::open(&db).unwrap());
    let session = LegacySession::new();
    assert!(session.confirm("unseen", vec![], vec![], &db).is_err());
    let preview = session.select_directory(&source).unwrap();
    assert!(
        !serde_json::to_string(&preview)
            .unwrap()
            .contains("synthetic-secret")
    );
    assert_eq!(preview.searches.len(), 1);
    assert!(
        session
            .confirm(
                "wrong-digest",
                vec![preview.searches[0].id.clone()],
                vec![],
                &db
            )
            .is_err()
    );
    let preview = session.select_directory(&source).unwrap();
    let report = session
        .confirm(
            &preview.digest,
            vec![preview.searches[0].id.clone()],
            vec![],
            &db,
        )
        .unwrap();
    assert_eq!(report.imported_searches, 1);
    assert!(
        session
            .confirm(
                &preview.digest,
                vec![preview.searches[0].id.clone()],
                vec![],
                &db
            )
            .is_err()
    );
    assert_eq!(std::fs::read(&file).unwrap(), original);
    assert_eq!(
        StateStore::open(&db)
            .unwrap()
            .saved_searches()
            .unwrap()
            .len(),
        1
    );
}
#[tokio::test]
async fn import_uses_live_database_actor_and_checkpointed_destination_without_shutdown() {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().join("rust");
    let source = dir.path().join("legacy");
    std::fs::create_dir(&source).unwrap();
    std::fs::write(source.join("settings.json"),serde_json::to_vec(&serde_json::json!({"SavedSearches":"[{\"Name\":\"Actor import\",\"IncludeTags\":[\"fox\"]}]"})).unwrap()).unwrap();
    let db = furchive_app::Database::open(&root).await.unwrap();
    let session = LegacySession::new();
    let preview = session.select_directory(&source).unwrap();
    let confirmed = session
        .confirmed(
            &preview.digest,
            vec![preview.searches[0].id.clone()],
            vec![],
        )
        .unwrap();
    let report = db.import_legacy(confirmed).await.unwrap();
    assert_eq!(report.imported_searches, 1);
    assert_eq!(db.searches().await.unwrap()[0].name, "Actor import");
    db.shutdown().await.unwrap();
}
#[test]
fn replacing_or_clearing_preview_revokes_prior_confirmation() {
    let dir = tempfile::tempdir().unwrap();
    let source = dir.path().join("legacy");
    std::fs::create_dir(&source).unwrap();
    std::fs::write(source.join("settings.json"), b"{}").unwrap();
    let session = LegacySession::new();
    let preview = session.select_directory(&source).unwrap();
    session.clear().unwrap();
    let db = dir.path().join("state.sqlite");
    drop(StateStore::open(&db).unwrap());
    assert!(
        session
            .confirm(&preview.digest, vec!["anything".into()], vec![], &db)
            .is_err()
    );
}
