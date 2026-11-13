//! REY-META-01: prior final receipts are not proof of a surviving stage.
use super::*;

fn snapshot_intent(id: &str, policy: DuplicatePolicy) -> Intent {
    let mut media = MediaItem::new(PostKey::new("e621", id).unwrap());
    media.full_url = Some("https://static1.e621.net/data/test.jpg".into());
    intent(id, policy).with_metadata(&media).unwrap()
}

async fn written(temp: &OwnedTemp) -> (std::path::PathBuf, i64, Vec<u8>) {
    let server = fixture(vec![response("owned")]).await;
    let db = temp.database("repair");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &server.origin).unwrap());
    let id = engine
        .enqueue(snapshot_intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    worker.shutdown().await.unwrap();
    assert_eq!(
        engine.jobs().unwrap()[0].metadata_status,
        MetadataStatus::Written
    );
    let bytes = std::fs::read(temp.path().join("1.jpg.metadata.json")).unwrap();
    (db, id, bytes)
}

#[tokio::test]
async fn written_retry_preserves_hostile_stage_and_target_then_restores_after_removal() {
    for kind in [
        "stage-partial",
        "stage-matching-unclaimed",
        "stage-symlink",
        "target",
    ] {
        let temp = OwnedTemp::new();
        let (db, id, bytes) = written(&temp).await;
        let side = temp.path().join("1.jpg.metadata.json");
        // Keep the previous final identity alive while inserting an unclaimed stage.
        let _held_sidecar = std::fs::File::open(&side).unwrap();
        std::fs::remove_file(&side).unwrap();
        let stage = temp.path().join(format!(".furchive-metadata-{id}.tmp"));
        let blocked = if kind == "target" { &side } else { &stage };
        let sentinel = if kind == "stage-matching-unclaimed" {
            bytes.clone()
        } else {
            b"partial".to_vec()
        };
        if kind == "stage-symlink" {
            let outside = temp.path().join("outside");
            std::fs::write(&outside, &sentinel).unwrap();
            symlink(&outside, blocked).unwrap();
        } else {
            std::fs::write(blocked, &sentinel).unwrap();
        }
        let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
        let worker = engine.start().unwrap();
        let refused = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(refused.metadata_status, MetadataStatus::Failed, "{kind}");
        assert_eq!(refused.state, State::Completed);
        assert_eq!(std::fs::read(blocked).unwrap(), sentinel);
        std::fs::remove_file(blocked).unwrap();
        let restored = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(
            restored.metadata_status,
            MetadataStatus::Written,
            "{kind}: {:?}",
            restored.metadata_error
        );
        assert_eq!(restored.metadata_attempts, 2);
        assert_eq!(std::fs::read(&side).unwrap(), bytes);
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
        assert!(worker.control(id, Control::RetryMetadata).await.is_err());
        worker.shutdown().await.unwrap();
        drop(engine);
        let engine = Engine::open(&db, temp.path()).unwrap();
        assert_eq!(
            engine.jobs().unwrap()[0].metadata_status,
            MetadataStatus::Written
        );
        assert_eq!(engine.jobs().unwrap()[0].metadata_attempts, 2);
    }
}

#[tokio::test]
async fn missing_written_retry_refuses_missing_replaced_and_mutated_media() {
    for kind in ["missing", "replaced", "mutated"] {
        let temp = OwnedTemp::new();
        let (db, id, _) = written(&temp).await;
        let side = temp.path().join("1.jpg.metadata.json");
        std::fs::remove_file(&side).unwrap();
        let media = temp.path().join("1.jpg");
        // Keep original identity alive so replacement cannot reuse its inode.
        let held = std::fs::File::open(&media).unwrap();
        if kind == "mutated" {
            std::fs::write(&media, b"other").unwrap();
        } else {
            std::fs::remove_file(&media).unwrap();
            if kind == "replaced" {
                std::fs::write(&media, b"owned").unwrap();
            }
        }
        let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
        let worker = engine.start().unwrap();
        let j = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(j.state, State::Completed);
        assert_eq!(j.metadata_status, MetadataStatus::Failed, "{kind}");
        assert!(!side.exists());
        assert!(!temp
            .path()
            .join(format!(".furchive-metadata-{id}.tmp"))
            .exists());
        assert_eq!(media.exists(), kind != "missing");
        if kind != "missing" {
            assert_eq!(
                std::fs::read(&media).unwrap(),
                if kind == "mutated" {
                    b"other"
                } else {
                    b"owned"
                }
            );
        }
        worker.shutdown().await.unwrap();
        drop(held);
    }
}

#[tokio::test]
async fn removed_tampered_written_sidecar_and_failed_ack_can_retry_offline() {
    for ack_failure in [false, true] {
        let temp = OwnedTemp::new();
        let (db, id, expected) = written(&temp).await;
        let side = temp.path().join("1.jpg.metadata.json");
        if ack_failure {
            let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
            let mut j = engine.jobs().unwrap().remove(0);
            j.metadata_status = MetadataStatus::Pending;
            engine.save(&j).unwrap();
            engine.db.lock().unwrap().execute_batch("CREATE TRIGGER repair_ack_failure BEFORE UPDATE ON jobs WHEN json_extract(NEW.payload, '$.metadata_status') = 'Written' BEGIN SELECT RAISE(FAIL, 'injected ack failure'); END;").unwrap();
            engine.finish_metadata(&mut j).unwrap();
            assert_eq!(j.metadata_status, MetadataStatus::Failed);
            engine
                .db
                .lock()
                .unwrap()
                .execute_batch("DROP TRIGGER repair_ack_failure")
                .unwrap();
        } else {
            std::fs::write(&side, b"tampered").unwrap();
            let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
            let worker = engine.start().unwrap();
            assert_eq!(
                worker
                    .control(id, Control::RetryMetadata)
                    .await
                    .unwrap()
                    .metadata_status,
                MetadataStatus::Failed
            );
            assert_eq!(std::fs::read(&side).unwrap(), b"tampered");
            worker.shutdown().await.unwrap();
        }
        std::fs::remove_file(&side).unwrap();
        let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
        let worker = engine.start().unwrap();
        assert_eq!(
            worker
                .control(id, Control::RetryMetadata)
                .await
                .unwrap()
                .metadata_status,
            MetadataStatus::Written
        );
        assert_eq!(std::fs::read(&side).unwrap(), expected);
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
        worker.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn missing_written_retry_attempt_cap_survives_restarts() {
    let temp = OwnedTemp::new();
    let (db, id, _) = written(&temp).await;
    let side = temp.path().join("1.jpg.metadata.json");
    for attempt in 1..=3 {
        std::fs::remove_file(&side).unwrap();
        let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
        let worker = engine.start().unwrap();
        let j = worker.control(id, Control::RetryMetadata).await.unwrap();
        assert_eq!(j.metadata_status, MetadataStatus::Written);
        assert_eq!(j.metadata_attempts, attempt);
        worker.shutdown().await.unwrap();
    }
    std::fs::remove_file(&side).unwrap();
    let engine = std::sync::Arc::new(Engine::open(&db, temp.path()).unwrap());
    let worker = engine.start().unwrap();
    assert!(worker.control(id, Control::RetryMetadata).await.is_err());
    assert_eq!(engine.jobs().unwrap()[0].metadata_attempts, 3);
    assert_eq!(
        engine.jobs().unwrap()[0].metadata_status,
        MetadataStatus::Failed
    );
    assert!(!side.exists());
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn missing_written_retry_kill_child() {
    let Ok(base) = std::env::var("FURCHIVE_METADATA_RETRY_ROOT") else {
        return;
    };
    let root = std::path::Path::new(&base);
    let phase = std::env::var("FURCHIVE_METADATA_CHILD_PHASE")
        .unwrap()
        .parse()
        .unwrap();
    let engine = std::sync::Arc::new(
        Engine::open(root.parent().unwrap().join("state/repair"), root).unwrap(),
    );
    engine
        .crash_at
        .store(phase, std::sync::atomic::Ordering::SeqCst);
    let worker = engine.start().unwrap();
    worker.control(1, Control::RetryMetadata).await.unwrap();
    panic!("retry child did not reach kill boundary");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn missing_written_retry_real_kills_preserve_durable_stage_boundaries_offline() {
    for phase in [10, 11, 12, 7, 8] {
        let temp = OwnedTemp::new();
        let (db, id, expected) = written(&temp).await;
        let media = std::fs::File::open(temp.path().join("1.jpg")).unwrap();
        let media_id = crate::files::identity(&media).unwrap();
        let side = temp.path().join("1.jpg.metadata.json");
        std::fs::remove_file(&side).unwrap();
        let mut child = std::process::Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "tests::metadata_repair::missing_written_retry_kill_child",
                "--nocapture",
            ])
            .env("FURCHIVE_METADATA_RETRY_ROOT", temp.path())
            .env("FURCHIVE_METADATA_CHILD_ROOT", temp.path())
            .env("FURCHIVE_METADATA_CHILD_PHASE", phase.to_string())
            .stdout(std::process::Stdio::null())
            .spawn()
            .unwrap();
        let reached = tokio::time::timeout(std::time::Duration::from_secs(8), async {
            while !temp.path().join("kill-ready").exists() {
                assert!(
                    child.try_wait().unwrap().is_none(),
                    "child exited before seam {phase}"
                );
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        })
        .await;
        child.kill().unwrap();
        child.wait().unwrap();
        reached.expect("child must reach durable retry kill seam");
        let stage = temp.path().join(format!(".furchive-metadata-{id}.tmp"));
        let boundary_stage = std::fs::read(&stage).ok();
        let engine = Engine::open(&db, temp.path()).unwrap();
        let j = engine.jobs().unwrap().remove(0);
        assert_eq!(j.state, State::Completed);
        assert_eq!(j.metadata_attempts, 1);
        if phase == 11 || phase == 12 {
            assert_eq!(j.metadata_status, MetadataStatus::Failed);
            assert_eq!(std::fs::read(&stage).unwrap(), boundary_stage.unwrap());
            assert!(!side.exists());
        } else {
            assert_eq!(
                j.metadata_status,
                MetadataStatus::Written,
                "phase {phase}: {:?}",
                j.metadata_error
            );
            assert_eq!(std::fs::read(&side).unwrap(), expected);
            assert!(!stage.exists());
        }
        assert_eq!(
            crate::files::identity(&std::fs::File::open(temp.path().join("1.jpg")).unwrap())
                .unwrap(),
            media_id
        );
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"owned");
        drop(engine);
        let engine = Engine::open(&db, temp.path()).unwrap();
        assert_eq!(engine.jobs().unwrap()[0].metadata_status, j.metadata_status);
    }
}
