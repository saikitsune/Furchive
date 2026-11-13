//! Safety regressions for optional metadata; media success is independent.
use super::*;

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn retry_metadata_refuses_same_directory_completed_but_still_joined_writer() {
    let server = fixture(vec![response("owned"), response("other")]).await;
    let temp = OwnedTemp::new();
    std::fs::write(temp.path().join("1.jpg.metadata.json"), b"collision").unwrap();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("q"), temp.path(), &server.origin).unwrap(),
    );
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start_with_concurrency(2).unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while engine.jobs().unwrap()[0].metadata_status != MetadataStatus::Failed {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    engine
        .crash_at
        .store(9, std::sync::atomic::Ordering::SeqCst);
    let next = engine
        .enqueue(snapshot_intent("2", DuplicatePolicy::Skip))
        .unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while !temp.path().join("metadata-writer-held").exists() {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(engine.jobs().unwrap()[1].state, State::Completed);
    let result = worker.control(id, Control::RetryMetadata).await;
    std::fs::write(temp.path().join("metadata-writer-release"), b"release").unwrap();
    worker.shutdown().await.unwrap();
    assert!(
        result.is_err(),
        "retry overlapped Completed writer before join"
    );
    assert_eq!(
        engine
            .jobs()
            .unwrap()
            .iter()
            .find(|j| j.id == next)
            .unwrap()
            .metadata_status,
        MetadataStatus::Written
    );
}

#[tokio::test]
async fn rejected_metadata_retry_never_recreates_missing_media_directories() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("q"), temp.path(), &server.origin).unwrap(),
    );
    let mut download = snapshot_intent("1", DuplicatePolicy::Skip);
    download.relative = "nested/1.jpg".into();
    let id = engine.enqueue(download).unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    let mut job = engine.jobs().unwrap().remove(0);
    job.metadata_status = MetadataStatus::Failed;
    job.metadata_error = Some("test refused".into());
    engine.save(&job).unwrap();
    std::fs::rename(temp.path().join("nested"), temp.path().join("retained")).unwrap();
    let _ = worker.control(id, Control::RetryMetadata).await;
    assert!(
        !temp.path().join("nested").exists(),
        "retry created missing media parent before verification"
    );
    assert_eq!(
        std::fs::read(temp.path().join("retained/1.jpg")).unwrap(),
        b"owned"
    );
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn retry_metadata_refuses_while_same_directory_transfer_is_running() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let release = std::sync::Arc::new(tokio::sync::Notify::new());
    let gate = release.clone();
    let (sent, seen) = tokio::sync::oneshot::channel();
    let server = tokio::spawn(async move {
        let (mut first, _) = listener.accept().await.unwrap();
        let mut b = [0; 4096];
        assert!(first.read(&mut b).await.unwrap() > 0);
        first.write_all(response("owned").as_bytes()).await.unwrap();
        drop(first);
        let (mut second, _) = listener.accept().await.unwrap();
        assert!(second.read(&mut b).await.unwrap() > 0);
        let _ = sent.send(());
        gate.notified().await;
        second
            .write_all(response("other").as_bytes())
            .await
            .unwrap();
    });
    let temp = OwnedTemp::new();
    std::fs::write(temp.path().join("1.jpg.metadata.json"), b"collision").unwrap();
    let engine =
        std::sync::Arc::new(Engine::open_test(temp.database("q"), temp.path(), &origin).unwrap());
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start_with_concurrency(2).unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    std::fs::remove_file(temp.path().join("1.jpg.metadata.json")).unwrap();
    let next = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    seen.await.unwrap();
    assert!(
        worker.control(id, Control::RetryMetadata).await.is_err(),
        "retry raced active native directory writer"
    );
    release.notify_one();
    assert_eq!(terminal(&engine, next).await.state, State::Completed);
    assert_eq!(
        worker
            .control(id, Control::RetryMetadata)
            .await
            .unwrap()
            .metadata_status,
        MetadataStatus::Written
    );
    worker.shutdown().await.unwrap();
    server.await.unwrap();
}

#[test]
fn snapshot_nested_identity_and_intent_unknown_fields_are_rejected() {
    let good = serde_json::to_value(snapshot_intent("1", DuplicatePolicy::Skip)).unwrap();
    for nested in [true, false] {
        let mut bad = good.clone();
        if nested {
            bad["metadata"]["key"]["headers"] = serde_json::json!("secret");
        } else {
            bad["headers"] = serde_json::json!("secret");
        }
        assert!(
            serde_json::from_value::<Intent>(bad).is_err(),
            "unknown field accepted (nested {nested})"
        );
    }
}

#[test]
fn sidecar_publish_refuses_substituted_staging_identity() {
    use std::io::Write;
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    let (dir, _) = grant.parent("1.jpg").unwrap();
    let mut file = grant
        .metadata_stage(&dir, ".furchive-metadata-1.tmp", None, b"trusted")
        .unwrap();
    file.write_all(b"trusted").unwrap();
    file.sync_all().unwrap();
    std::fs::rename(
        temp.path().join(".furchive-metadata-1.tmp"),
        temp.path().join("keep-stage"),
    )
    .unwrap();
    std::fs::write(temp.path().join(".furchive-metadata-1.tmp"), b"unrelated").unwrap();
    assert!(grant
        .publish_metadata(
            &dir,
            ".furchive-metadata-1.tmp",
            &file,
            "1.jpg.metadata.json"
        )
        .is_err());
    assert!(!temp.path().join("1.jpg.metadata.json").exists());
    assert_eq!(
        std::fs::read(temp.path().join("keep-stage")).unwrap(),
        b"trusted"
    );
}

#[tokio::test]
async fn metadata_ack_database_failure_never_marks_good_media_failed() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("q"), temp.path(), &server.origin).unwrap(),
    );
    engine.db.lock().unwrap().execute_batch("CREATE TRIGGER metadata_ack_failure BEFORE UPDATE ON jobs WHEN json_extract(NEW.payload, '$.metadata_status') = 'Written' BEGIN SELECT RAISE(FAIL, 'injected ack failure'); END;").unwrap();
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    let j = terminal(&engine, id).await;
    worker.shutdown().await.unwrap();
    assert_eq!(
        j.state,
        State::Completed,
        "metadata acknowledgement corrupted media state: {:?}",
        j.error
    );
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
    let j = engine.jobs().unwrap().remove(0);
    assert_ne!(j.metadata_status, MetadataStatus::Written);
}

#[tokio::test]
async fn matching_sidecar_is_idempotent_but_skip_and_old_jobs_never_export() {
    let server = fixture(vec![
        response("owned"),
        response("owned"),
        response("owned"),
    ])
    .await;
    let temp = OwnedTemp::new();
    let db = temp.database("q");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    let first = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, first).await.state, State::Completed);
    let side = temp.path().join("1.jpg.metadata.json");
    let bytes = std::fs::read(&side).unwrap();
    let original = std::fs::File::open(&side).unwrap();
    let identity = crate::files::identity(&original).unwrap();
    let second = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Overwrite))
        .unwrap();
    assert_eq!(terminal(&engine, second).await.state, State::Completed);
    assert_eq!(
        engine.jobs().unwrap()[1].metadata_status,
        MetadataStatus::Written
    );
    assert_eq!(
        crate::files::identity(&std::fs::File::open(&side).unwrap()).unwrap(),
        identity
    );
    assert_eq!(std::fs::read(&side).unwrap(), bytes);
    let skip = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    assert_eq!(terminal(&engine, skip).await.state, State::Skipped);
    assert!(worker.control(skip, Control::RetryMetadata).await.is_err());
    let old = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    assert_eq!(terminal(&engine, old).await.state, State::Completed);
    assert_eq!(
        engine.jobs().unwrap()[3].metadata_status,
        MetadataStatus::NotRequested
    );
    assert!(worker.control(old, Control::RetryMetadata).await.is_err());
    worker.shutdown().await.unwrap();
    drop(engine);
    let engine = Engine::open(&db, temp.path()).unwrap();
    assert_eq!(
        engine.jobs().unwrap()[3].metadata_status,
        MetadataStatus::NotRequested
    );
    assert!(!temp.path().join("2.jpg.metadata.json").exists());
}

#[tokio::test]
async fn retry_only_verifies_same_completed_media_and_never_changes_tampered_bytes() {
    for tamper in ["bytes", "identity", "symlink"] {
        let server = fixture(vec![response("owned")]).await;
        let temp = OwnedTemp::new();
        let db = temp.database("q");
        let side = temp.path().join("1.jpg.metadata.json");
        std::fs::write(&side, b"collision").unwrap();
        let engine =
            std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
        let id = engine
            .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
            .unwrap();
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Completed);
        assert_eq!(
            engine.jobs().unwrap()[0].metadata_status,
            MetadataStatus::Failed
        );
        std::fs::remove_file(&side).unwrap();
        let media = temp.path().join("1.jpg");
        let hold = std::fs::File::open(&media).unwrap();
        if tamper == "bytes" {
            std::fs::write(&media, b"other").unwrap();
        } else {
            std::fs::remove_file(&media).unwrap();
            if tamper == "identity" {
                std::fs::write(&media, b"owned").unwrap();
            } else {
                std::fs::write(temp.path().join("outside"), b"owned").unwrap();
                symlink(temp.path().join("outside"), &media).unwrap();
            }
        }
        let j = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(j.state, State::Completed);
        assert_eq!(j.metadata_status, MetadataStatus::Failed);
        assert!(!side.exists());
        assert_eq!(
            std::fs::read(&media).unwrap(),
            if tamper == "bytes" {
                b"other"
            } else {
                b"owned"
            }
        );
        worker.shutdown().await.unwrap();
        drop(hold);
    }
}

#[tokio::test]
async fn metadata_symlinks_hardlinks_and_unclaimed_stage_are_preserved_on_failure() {
    for kind in [
        "side-symlink",
        "side-hardlink",
        "stage-symlink",
        "stage-hardlink",
        "stage-unclaimed",
        "side-directory",
    ] {
        let server = fixture(vec![response("owned")]).await;
        let temp = OwnedTemp::new();
        let outside = temp.path().join("outside");
        std::fs::write(&outside, b"untouched outside").unwrap();
        let target = temp.path().join(if kind.starts_with("side") {
            "1.jpg.metadata.json"
        } else {
            ".furchive-metadata-1.tmp"
        });
        if kind.ends_with("symlink") {
            symlink(&outside, &target).unwrap();
        } else if kind.ends_with("hardlink") {
            std::fs::hard_link(&outside, &target).unwrap();
        } else if kind.ends_with("directory") {
            std::fs::create_dir(&target).unwrap();
        } else {
            std::fs::write(&target, b"unclaimed").unwrap();
        }
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("q"), temp.path(), &server.origin).unwrap(),
        );
        let id = engine
            .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
            .unwrap();
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Completed);
        assert_eq!(
            engine.jobs().unwrap()[0].metadata_status,
            MetadataStatus::Failed,
            "{kind}"
        );
        assert_eq!(std::fs::read(&outside).unwrap(), b"untouched outside");
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
        if !kind.ends_with("directory") {
            assert_eq!(
                std::fs::read(&target).unwrap(),
                if kind.ends_with("unclaimed") {
                    b"unclaimed".as_slice()
                } else {
                    b"untouched outside".as_slice()
                }
            );
        }
        worker.shutdown().await.unwrap();
    }
}

#[test]
fn metadata_suffix_and_reserved_stage_names_cannot_mutate_database_or_engine_namespace() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("q"), temp.path()).unwrap();
    for path in [
        ".furchive-metadata-2.tmp",
        "folder/.furchive-metadata-1.tmp",
        ".FURCHIVE-METADATA-1.TMP",
    ] {
        let mut i = snapshot_intent("1", DuplicatePolicy::Skip);
        i.relative = path.into();
        assert!(engine.enqueue_paused(i).is_err());
    }
    assert!(engine.jobs().unwrap().is_empty());
}

#[tokio::test]
async fn tampered_stage_receipt_refuses_recovery_and_leaves_good_art_completed() {
    let server = fixture(vec![response("owned")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("q");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    engine
        .crash_at
        .store(5, std::sync::atomic::Ordering::SeqCst);
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    worker.shutdown().await.unwrap();
    let mut j = engine.jobs().unwrap().remove(0);
    j.metadata_status = MetadataStatus::Pending;
    j.metadata_error = None;
    engine.save(&j).unwrap();
    drop(engine);
    let stage = temp.path().join(".furchive-metadata-1.tmp");
    std::fs::write(&stage, b"hostile").unwrap();
    let engine = Engine::open(&db, temp.path()).unwrap();
    let j = engine.jobs().unwrap().remove(0);
    assert_eq!(j.state, State::Completed);
    assert_eq!(j.metadata_status, MetadataStatus::Failed);
    assert_eq!(std::fs::read(&stage).unwrap(), b"hostile");
    assert!(!temp.path().join("1.jpg.metadata.json").exists());
}
