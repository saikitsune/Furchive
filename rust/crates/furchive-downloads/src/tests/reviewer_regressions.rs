//! Reviewer-owned adversarial cases. Product source is unchanged; this module is injected only into probe-source.
use crate::tests::*;
use crate::{MetadataStatus, METADATA_LIMIT};

#[tokio::test]
async fn review_missing_written_sidecar_explicit_retry_restores_without_network() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("review-retry");
    let mut media = MediaItem::new(PostKey::new("e621", "1").unwrap());
    media.full_url = Some("https://static1.e621.net/data/test.jpg".into());
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    let id = engine.enqueue(intent("1", DuplicatePolicy::Skip).with_metadata(&media).unwrap()).unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    worker.shutdown().await.unwrap();
    assert_eq!(engine.jobs().unwrap()[0].metadata_status, MetadataStatus::Written);
    let side = temp.path().join("1.jpg.metadata.json");
    let expected = std::fs::read(&side).unwrap();
    drop(engine);
    drop(server); // Explicit recovery must not fetch any media.
    std::fs::remove_file(&side).unwrap();
    let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
    assert_eq!(engine.jobs().unwrap()[0].metadata_status, MetadataStatus::Failed);
    let worker = engine.start().unwrap();
    let retried = worker.control(id, Control::RetryMetadata).await.unwrap();
    worker.shutdown().await.unwrap();
    assert_eq!(retried.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
    assert_eq!(retried.metadata_status, MetadataStatus::Written, "retry failed despite unchanged verified media and absent target/stage: {:?}", retried.metadata_error);
    assert_eq!(std::fs::read(&side).unwrap(), expected);
}

#[test]
fn review_snapshot_rejects_url_secrets_and_keeps_exact_limit_bound() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("snapshot"), temp.path()).unwrap();
    let mut m = MediaItem::new(PostKey::new("e621", "1").unwrap());
    m.full_url = Some("https://static1.e621.net/data/test.jpg".into());
    let good = serde_json::to_value(intent("1", DuplicatePolicy::Skip).with_metadata(&m).unwrap()).unwrap();
    for url in ["https://u:p@static1.e621.net/data/test.jpg", "https://static1.e621.net/data/test.jpg?api_key=synthetic", "https://static1.e621.net/data/test.jpg#synthetic", "file:///private/test.jpg", "https://evil.invalid/test.jpg"] {
        let mut bad=good.clone();bad["metadata"]["url"]=serde_json::json!(url);bad["url"]=serde_json::json!(url);
        assert!(serde_json::from_value::<Intent>(bad).is_err(),"accepted secret/noncanonical URL: {url}");
        assert!(engine.jobs().unwrap().is_empty());
    }
    m.description="x".repeat(METADATA_LIMIT);
    assert!(intent("1",DuplicatePolicy::Skip).with_metadata(&m).is_err());
}
