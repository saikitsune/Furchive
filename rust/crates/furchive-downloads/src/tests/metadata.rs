#[tokio::test]
async fn metadata_process_kill_child() {
    let Ok(base) = std::env::var("FURCHIVE_METADATA_CHILD_ROOT") else {
        return;
    };
    let root = std::path::Path::new(&base);
    let origin = std::env::var("FURCHIVE_METADATA_CHILD_ORIGIN").unwrap();
    let phase = std::env::var("FURCHIVE_METADATA_CHILD_PHASE")
        .unwrap()
        .parse()
        .unwrap();
    let engine = std::sync::Arc::new(
        Engine::open_test(root.parent().unwrap().join("state/db"), root, &origin).unwrap(),
    );
    engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    engine
        .crash_at
        .store(phase, std::sync::atomic::Ordering::SeqCst);
    let _worker = engine.start().unwrap();
    tokio::time::sleep(std::time::Duration::from_secs(20)).await;
    panic!("metadata child did not reach kill boundary");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn metadata_real_process_kill_before_and_after_sidecar_publication_recovers_offline() {
    for phase in [7, 8] {
        let server = fixture(vec![response("owned")]).await;
        let temp = OwnedTemp::new();
        let mut child = std::process::Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "tests::metadata::metadata_process_kill_child",
                "--nocapture",
            ])
            .env("FURCHIVE_METADATA_CHILD_ROOT", temp.path())
            .env("FURCHIVE_METADATA_CHILD_ORIGIN", &server.origin)
            .env("FURCHIVE_METADATA_CHILD_PHASE", phase.to_string())
            .stdout(std::process::Stdio::null())
            .spawn()
            .unwrap();
        let reached = tokio::time::timeout(std::time::Duration::from_secs(8), async {
            loop {
                let db = temp.database("db");
                if db.exists() {
                    let c = rusqlite::Connection::open(&db).unwrap();
                    if let Ok(text) =
                        c.query_row("SELECT payload FROM jobs", [], |r| r.get::<_, String>(0))
                    {
                        if let Ok(j) = serde_json::from_str::<Job>(&text) {
                            let boundary = if phase == 7 {
                                temp.path().join(".furchive-metadata-1.tmp")
                            } else {
                                temp.path().join("1.jpg.metadata.json")
                            };
                            if j.state == State::Completed
                                && j.metadata_status == MetadataStatus::Pending
                                && j.metadata_publication.is_some()
                                && boundary.metadata().is_ok_and(|m| m.len() > 0)
                                && temp.path().join("kill-ready").exists()
                            {
                                break;
                            }
                        }
                    }
                }
                assert!(
                    child.try_wait().unwrap().is_none(),
                    "child exited before durable kill seam"
                );
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        })
        .await;
        child.kill().unwrap();
        child.wait().unwrap();
        reached.unwrap();
        let engine = Engine::open(temp.database("db"), temp.path()).unwrap();
        let j = engine.jobs().unwrap().remove(0);
        assert_eq!(j.state, State::Completed);
        assert_eq!(j.metadata_status, MetadataStatus::Written);
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
    }
}

use super::*;
#[path = "metadata_safety.rs"]
mod safety;

#[test]
fn metadata_recovery_stage_never_recreates_or_rewrites_an_unrelated_file() {
    use std::io::Write;
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    let (dir, _) = grant.parent("1.jpg").unwrap();
    let bytes = b"admitted";
    let mut file = grant
        .metadata_stage(&dir, ".furchive-metadata-1.tmp", None, bytes)
        .unwrap();
    file.write_all(bytes).unwrap();
    file.sync_all().unwrap();
    let receipt = crate::model::Publication::Native {
        identity: crate::files::identity(&file).unwrap(),
        digest: {
            use sha2::Digest;
            format!("{:x}", sha2::Sha256::digest(bytes))
        },
    };
    drop(file);
    let mut restored = grant
        .metadata_stage(&dir, ".furchive-metadata-1.tmp", Some(&receipt), bytes)
        .unwrap();
    assert!(
        restored.write_all(b"mutated!").is_err(),
        "recovery reopened stage writable"
    );
    assert_eq!(
        std::fs::read(temp.path().join(".furchive-metadata-1.tmp")).unwrap(),
        bytes
    );
}

#[tokio::test]
async fn retry_metadata_on_running_media_cannot_interrupt_its_writer() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let release = std::sync::Arc::new(tokio::sync::Notify::new());
    let gate = release.clone();
    let (sent, seen) = tokio::sync::oneshot::channel();
    let server = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut b = [0; 4096];
        assert!(socket.read(&mut b).await.unwrap() > 0);
        socket
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\n")
            .await
            .unwrap();
        let _ = sent.send(());
        gate.notified().await;
        socket.write_all(b"owned").await.unwrap();
    });
    let temp = OwnedTemp::new();
    let engine =
        std::sync::Arc::new(Engine::open_test(temp.database("q"), temp.path(), &origin).unwrap());
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    seen.await.unwrap();
    assert!(worker.control(id, Control::RetryMetadata).await.is_err());
    release.notify_one();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    worker.shutdown().await.unwrap();
    server.await.unwrap();
}

fn snapshot_intent(id: &str, policy: DuplicatePolicy) -> Intent {
    let mut media = MediaItem::new(PostKey::new("e621", id).unwrap());
    media.full_url = Some("https://static1.e621.net/data/test.jpg".into());
    media.title = "Admission title".into();
    intent(id, policy).with_metadata(&media).unwrap()
}

#[tokio::test]
async fn normalized_sidecar_follows_exact_rename_and_survives_reopen() {
    let server = fixture(vec![response("new media")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("metadata");
    std::fs::write(temp.path().join("1.jpg"), b"old art").unwrap();
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Rename))
        .unwrap();
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let j = &engine.jobs().unwrap()[0];
            if j.state == State::Completed {
                break;
            }
            assert_ne!(j.state, State::Failed, "{:?}", j.error);
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    worker.shutdown().await.unwrap();
    let j = engine.jobs().unwrap().remove(0);
    assert_eq!(j.metadata_status, MetadataStatus::Written);
    assert_eq!(j.relative_path, "1 (1).jpg");
    let sidecar = std::fs::read(temp.path().join("1 (1).jpg.metadata.json")).unwrap();
    let json: serde_json::Value = serde_json::from_slice(&sidecar).unwrap();
    assert_eq!(json["representation"], "normalized-furchive-v1");
    assert_eq!(json["key"]["id"], "1");
    assert_eq!(json["title"], "Admission title");
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg")).unwrap(),
        b"old art"
    );
    assert!(!temp.path().join("1.jpg.metadata.json").exists());
    drop(engine);
    let reopened = Engine::open(&db, temp.path()).unwrap();
    assert_eq!(
        reopened.jobs().unwrap()[0].metadata_status,
        MetadataStatus::Written
    );
}

#[test]
fn metadata_deserialization_rejects_mismatch_unknown_fields_and_oversize_before_queue() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("q"), temp.path()).unwrap();
    let valid = serde_json::to_value(snapshot_intent("1", DuplicatePolicy::Skip)).unwrap();
    for (field, value) in [
        ("representation", serde_json::json!("raw")),
        (
            "url",
            serde_json::json!("https://static1.e621.net/data/other.jpg"),
        ),
        ("key", serde_json::json!({"source":"e621","id":"2"})),
        ("headers", serde_json::json!({"Authorization":"secret"})),
        ("title", serde_json::json!("x".repeat(METADATA_LIMIT))),
    ] {
        let mut bad = valid.clone();
        bad["metadata"][field] = value;
        let admitted = serde_json::from_value::<Intent>(bad).and_then(|i| {
            engine
                .enqueue_paused(i)
                .map_err(|e| <serde_json::Error as serde::de::Error>::custom(e.to_string()))
        });
        assert!(admitted.is_err(), "accepted {field}");
        assert!(engine.jobs().unwrap().is_empty());
    }
}

#[test]
fn persisted_metadata_status_cannot_claim_written_without_admitted_snapshot() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("q"), temp.path()).unwrap();
    engine
        .enqueue_paused(intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let mut j = engine.jobs().unwrap().remove(0);
    j.metadata_status = MetadataStatus::Written;
    let db = engine.db.lock().unwrap();
    db.execute(
        "UPDATE jobs SET payload=?",
        [serde_json::to_string(&j).unwrap()],
    )
    .unwrap();
    drop(db);
    assert!(
        engine.jobs().is_err(),
        "forged Written accepted without snapshot/receipt"
    );
}

#[tokio::test]
async fn metadata_crash_windows_reconcile_without_network_or_adopting_tampered_stage() {
    for crash in [1, 2, 3, 5, 6] {
        let server = fixture(vec![response("owned")]).await;
        let temp = OwnedTemp::new();
        let db = temp.database("q");
        let engine =
            std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
        engine
            .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
            .unwrap();
        engine
            .crash_at
            .store(crash, std::sync::atomic::Ordering::SeqCst);
        let worker = engine.start().unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            while engine.jobs().unwrap()[0].error.is_none()
                && engine.jobs().unwrap()[0].metadata_error.is_none()
            {
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        })
        .await
        .unwrap();
        worker.shutdown().await.unwrap();
        // Model the durable last state at process death: optional writer has not
        // acknowledged Failed (real kill regression below exercises this boundary).
        if crash >= 5 {
            let mut j = engine.jobs().unwrap().remove(0);
            j.metadata_status = MetadataStatus::Pending;
            j.metadata_error = None;
            engine.save(&j).unwrap();
        }
        drop(engine);
        let engine = Engine::open(&db, temp.path()).unwrap();
        let j = engine.jobs().unwrap().remove(0);
        assert_eq!(j.state, State::Completed, "crash {crash}");
        assert_eq!(
            j.metadata_status,
            MetadataStatus::Written,
            "crash {crash}: {:?}",
            j.metadata_error
        );
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
        let sidecar = std::fs::read(temp.path().join("1.jpg.metadata.json")).unwrap();
        drop(engine);
        let engine = Engine::open(&db, temp.path()).unwrap();
        assert_eq!(
            engine.jobs().unwrap()[0].metadata_status,
            MetadataStatus::Written
        );
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg.metadata.json")).unwrap(),
            sidecar
        );
    }
}

#[tokio::test]
async fn metadata_collision_failure_retry_is_bounded_and_never_redownloads_media() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("q");
    let side = temp.path().join("1.jpg.metadata.json");
    std::fs::write(&side, b"unrelated metadata").unwrap();
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while engine.jobs().unwrap()[0].metadata_status != MetadataStatus::Failed {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    let job = engine.jobs().unwrap().remove(0);
    assert_eq!(job.state, State::Completed);
    assert!(job.error.is_none());
    for _ in 0..3 {
        let j = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(j.metadata_status, MetadataStatus::Failed);
    }
    assert!(worker.control(id, Control::RetryMetadata).await.is_err());
    assert_eq!(std::fs::read(&side).unwrap(), b"unrelated metadata");
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
    worker.shutdown().await.unwrap();
    drop(engine);
    let reopened = Engine::open(&db, temp.path()).unwrap();
    assert_eq!(
        reopened.jobs().unwrap()[0].metadata_status,
        MetadataStatus::Failed
    );
}

#[tokio::test]
async fn metadata_written_sidecar_tamper_is_detected_on_reopen_without_media_failure() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("q");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while engine.jobs().unwrap()[0].metadata_status != MetadataStatus::Written {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    worker.shutdown().await.unwrap();
    drop(engine);
    std::fs::write(temp.path().join("1.jpg.metadata.json"), b"tampered").unwrap();
    let engine = Engine::open(&db, temp.path()).unwrap();
    let j = engine.jobs().unwrap().remove(0);
    assert_eq!(j.state, State::Completed);
    assert_eq!(j.metadata_status, MetadataStatus::Failed);
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg.metadata.json")).unwrap(),
        b"tampered"
    );
}
