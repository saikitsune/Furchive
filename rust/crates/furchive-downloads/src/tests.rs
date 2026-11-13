#[cfg(feature = "fixture-http")]
#[tokio::test]
async fn explicit_fixture_engine_downloads_real_bytes_and_refuses_nonloopback_origin() {
    let server = fixture(vec![response("owned fixture")]).await;
    let temp = OwnedTemp::new();
    for origin in [
        "http://evil.invalid:31337/",
        "https://127.0.0.1:31337/",
        "http://127.0.0.1/",
        "http://user@127.0.0.1:31337/",
        "http://127.0.0.1:31337/path",
    ] {
        assert!(Engine::fixture_http(temp.database("reject"), temp.path(), origin).is_err());
    }
    let engine = std::sync::Arc::new(
        Engine::fixture_http(
            temp.database("queue"),
            temp.path(),
            &format!("{}/", server.origin),
        )
        .unwrap(),
    );
    engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if engine.jobs().unwrap()[0].state == State::Completed {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    worker.shutdown().await.unwrap();
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg")).unwrap(),
        b"owned fixture"
    );
}

#[test]
fn restored_jobs_can_be_held_before_start_without_network_authority() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("queue"), temp.path()).unwrap();
    engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    engine.hold_restored().unwrap();
    assert_eq!(engine.jobs().unwrap()[0].state, State::Paused);
}
#[test]
fn paused_intent_is_durable_before_any_worker_can_fetch() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("queue"), temp.path()).unwrap();
    let id = engine
        .enqueue_paused(intent("1", DuplicatePolicy::Skip))
        .unwrap();
    assert_eq!(engine.jobs().unwrap()[0].id, id);
    assert_eq!(engine.jobs().unwrap()[0].state, State::Paused);
}
#[test]
fn malformed_persisted_group_fields_refuse_queue_read() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("q"), temp.path()).unwrap();
    let id = engine
        .enqueue_paused(intent("1", DuplicatePolicy::Skip))
        .unwrap();
    let text: String = engine
        .db
        .lock()
        .unwrap()
        .query_row("SELECT payload FROM jobs WHERE id=?", [id], |r| r.get(0))
        .unwrap();
    let mut json: serde_json::Value = serde_json::from_str(&text).unwrap();
    json["group_id"] = serde_json::json!("../bad");
    engine
        .db
        .lock()
        .unwrap()
        .execute(
            "UPDATE jobs SET payload=? WHERE id=?",
            rusqlite::params![json.to_string(), id],
        )
        .unwrap();
    assert!(engine.jobs().is_err());
}
#[test]
fn paused_group_is_atomic_ordered_and_retained_after_reopen() {
    let temp = OwnedTemp::new();
    let db = temp.database("queue");
    let engine = Engine::open(&db, temp.path()).unwrap();
    let ids = engine
        .enqueue_group_paused(
            "pool-3-page-0",
            "Fixture pool",
            vec![
                (0, intent("10", DuplicatePolicy::Skip)),
                (2, intent("30", DuplicatePolicy::Skip)),
            ],
        )
        .unwrap();
    let jobs = engine.jobs().unwrap();
    assert_eq!(
        jobs.iter().map(|j| j.group_position).collect::<Vec<_>>(),
        vec![Some(0), Some(2)]
    );
    assert_eq!(jobs[0].group_id.as_deref(), Some("pool-3-page-0"));
    assert!(jobs.iter().all(|j| j.state == State::Paused));
    let mut invalid = intent("2", DuplicatePolicy::Skip);
    invalid.relative = "../escape".into();
    assert!(engine
        .enqueue_group_paused(
            "bad",
            "bad",
            vec![(0, intent("1", DuplicatePolicy::Skip)), (1, invalid)]
        )
        .is_err());
    assert_eq!(engine.jobs().unwrap().len(), 2);
    drop(engine);
    let engine = Engine::open(&db, temp.path()).unwrap();
    assert_eq!(
        engine
            .jobs()
            .unwrap()
            .iter()
            .map(|j| j.id)
            .collect::<Vec<_>>(),
        ids
    );
}
struct OwnedTemp {
    _temp: tempfile::TempDir,
    media: std::path::PathBuf,
    state: std::path::PathBuf,
}
impl OwnedTemp {
    fn new() -> Self {
        let base = std::fs::canonicalize(std::env::temp_dir()).unwrap();
        let temp = tempfile::tempdir_in(base).unwrap();
        let media = temp.path().join("media");
        let state = temp.path().join("state");
        std::fs::create_dir(&media).unwrap();
        std::fs::create_dir(&state).unwrap();
        Self {
            _temp: temp,
            media,
            state,
        }
    }
    fn path(&self) -> &std::path::Path {
        &self.media
    }
    fn database(&self, name: &str) -> std::path::PathBuf {
        self.state.join(name)
    }
}

#[tokio::test]
async fn renamed_nested_publication_survives_crash_after_collision() {
    let fixture = fixture(vec![response("new")]).await;
    let temp = OwnedTemp::new();
    std::fs::create_dir(temp.path().join("nested")).unwrap();
    std::fs::write(temp.path().join("nested/1.jpg"), b"original").unwrap();
    let db = temp.database("db");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &fixture.origin).unwrap());
    let mut value = intent("1", DuplicatePolicy::Rename);
    value.relative = "nested/1.jpg".into();
    engine.enqueue(value).unwrap();
    // New hook: fail before the successful suffix rename, after the first refusal.
    engine
        .crash_at
        .store(4, std::sync::atomic::Ordering::SeqCst);
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if engine.jobs().unwrap()[0].error.is_some() {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    worker.shutdown().await.unwrap();
    let job = engine.jobs().unwrap().remove(0);
    assert_eq!(job.state, State::Publishing);
    assert_eq!(job.relative_path, "nested/1 (1).jpg");
    assert!(
        job.publication.is_some(),
        "suffix lost publication identity/digest"
    );
    drop(engine);
    let reopened = Engine::open(&db, temp.path()).unwrap();
    assert_eq!(reopened.jobs().unwrap()[0].state, State::Completed);
    assert_eq!(
        std::fs::read(temp.path().join("nested/1.jpg")).unwrap(),
        b"original"
    );
    assert_eq!(
        std::fs::read(temp.path().join("nested/1 (1).jpg")).unwrap(),
        b"new"
    );
}

#[test]
fn platform_adapter_atomic_publication_keeps_open_original_and_identity() {
    use std::io::Write;
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    std::fs::write(temp.path().join("original.jpg"), b"original").unwrap();
    let mut part = grant.part(91).unwrap();
    part.write_all(b"replacement").unwrap();
    part.sync_all().unwrap();
    let identity = crate::files::identity(&part).unwrap();
    let (parent, name) = grant.parent("original.jpg").unwrap();
    assert_eq!(
        grant
            .publish(91, &part, &parent, &name, false)
            .unwrap_err()
            .kind(),
        std::io::ErrorKind::AlreadyExists
    );
    assert_eq!(
        std::fs::read(temp.path().join("original.jpg")).unwrap(),
        b"original"
    );
    let original = grant.destination("original.jpg").unwrap();
    grant.publish(91, &part, &parent, &name, true).unwrap();
    crate::files::sync_directory(&parent).unwrap();
    crate::files::sync_directory(&grant.stage).unwrap();
    assert_eq!(
        crate::files::identity(&grant.destination("original.jpg").unwrap()).unwrap(),
        identity
    );
    assert_ne!(crate::files::identity(&original).unwrap(), identity);
    use std::io::Read;
    let mut original_bytes = Vec::new();
    (&original).read_to_end(&mut original_bytes).unwrap();
    assert_eq!(original_bytes, b"original");
    assert_eq!(
        std::fs::read(temp.path().join("original.jpg")).unwrap(),
        b"replacement"
    );
}

use super::*;

mod native;

fn symlink(
    source: impl AsRef<std::path::Path>,
    target: impl AsRef<std::path::Path>,
) -> std::io::Result<()> {
    #[cfg(unix)]
    {
        std::os::unix::fs::symlink(source, target)
    }
    #[cfg(windows)]
    {
        if source.as_ref().is_dir() {
            std::os::windows::fs::symlink_dir(source, target)
        } else {
            std::os::windows::fs::symlink_file(source, target)
        }
    }
}

#[test]
fn publication_receipts_read_legacy_unix_and_full_windows_identity() {
    let legacy: crate::model::Publication = serde_json::from_str("[42,99,\"digest\"]").unwrap();
    assert_eq!(
        legacy.parts(),
        (
            crate::files::FileIdentity::Unix {
                device: 42,
                inode: 99
            },
            "digest".into()
        )
    );
    let identity = crate::files::FileIdentity::Windows {
        volume: u64::MAX,
        file: [0xff; 16],
    };
    let receipt = crate::model::Publication::Native {
        identity: identity.clone(),
        digest: "hash".into(),
    };
    let decoded: crate::model::Publication =
        serde_json::from_str(&serde_json::to_string(&receipt).unwrap()).unwrap();
    assert_eq!(decoded.parts(), (identity, "hash".into()));
}

#[test]
#[cfg(target_os = "linux")]
fn filesystem_case_alias_cannot_overwrite_database_or_companions() {
    let temp = OwnedTemp::new();
    std::fs::create_dir(temp.path().join("nested")).unwrap();
    let db = temp.path().join("nested/jobs.db");
    let engine = Engine::open(&db, temp.path()).unwrap();
    for path in [
        "nested/jobs.db",
        "nested/jobs.db-wal",
        "NESTED/JOBS.DB",
        "NESTED/JOBS.DB-WAL",
    ] {
        let mut value = intent("1", DuplicatePolicy::Overwrite);
        value.relative = path.into();
        assert!(
            engine.enqueue(value).is_err(),
            "database alias admitted: {path}"
        );
    }
}

#[test]
fn stage_alias_is_rejected_by_directory_identity_not_spelling() {
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    // Test the same opened staging object under a different logical root: the
    // guard must use identity, not just reserved spelling. No symlink following.
    let (parent, _) = grant.parent("normal/file.jpg").unwrap();
    assert!(grant.ensure_media_directory(&parent).is_ok());
    assert!(grant.ensure_media_directory(&grant.stage).is_err());
}

#[test]
fn stage_substitution_never_follows_outside_objects() {
    let temp = OwnedTemp::new();
    let outside = temp.path().join("outside");
    std::fs::create_dir(&outside).unwrap();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    symlink(&outside, root.join(".furchive-downloads")).unwrap();
    assert!(Engine::open(temp.database("db"), &root).is_err());
    assert!(std::fs::read_dir(&outside).unwrap().next().is_none());
}

#[test]
fn staging_case_alias_cannot_be_enqueued() {
    for path in [
        ".FURCHIVE-DOWNLOADS/1.part",
        ".Furchive-Downloads/database.identity",
    ] {
        assert!(crate::files::relative(path).is_err());
    }
}

#[tokio::test]
async fn rename_with_unfit_extension_fails_only_job_and_keeps_queue_reopenable() {
    let fixture = fixture(vec![response("new"), response("next")]).await;
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let db = temp.database("jobs.sqlite");
    let name = format!("a.{}", "x".repeat(253));
    std::fs::write(root.join(&name), b"original").unwrap();
    let engine = std::sync::Arc::new(Engine::open_test(&db, &root, &fixture.origin).unwrap());
    let mut value = intent("1", DuplicatePolicy::Rename);
    value.relative = name.clone();
    let first = engine.enqueue(value).unwrap();
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, first).await.state, State::Failed);
    assert_eq!(terminal(&engine, second).await.state, State::Completed);
    worker.shutdown().await.unwrap();
    drop(engine);
    assert_eq!(Engine::open(&db, &root).unwrap().jobs().unwrap().len(), 2);
    assert_eq!(std::fs::read(root.join(name)).unwrap(), b"original");
}

#[tokio::test]
async fn maximum_length_rename_preserves_queue_reopen_and_next_job() {
    let fixture = fixture(vec![response("new"), response("next")]).await;
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let db = temp.database("jobs.sqlite");
    let name = format!("{}.jpg", "a".repeat(251));
    assert_eq!(name.len(), 255);
    std::fs::write(root.join(&name), b"original").unwrap();
    let engine = std::sync::Arc::new(Engine::open_test(&db, &root, &fixture.origin).unwrap());
    let mut value = intent("1", DuplicatePolicy::Rename);
    value.relative = name.clone();
    let first = engine.enqueue(value).unwrap();
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    let renamed = terminal(&engine, first).await;
    assert_eq!(renamed.state, State::Completed);
    assert!(renamed.relative_path.len() <= 255);
    assert_eq!(
        std::fs::read(root.join(&renamed.relative_path)).unwrap(),
        b"new"
    );
    assert_eq!(terminal(&engine, second).await.state, State::Completed);
    assert_eq!(std::fs::read(root.join(name)).unwrap(), b"original");
    worker.shutdown().await.unwrap();
    drop(engine);
    assert_eq!(Engine::open(&db, &root).unwrap().jobs().unwrap().len(), 2);
}

use furchive_domain::{FilenameTemplate, MediaItem, PostKey, TemplateValues};

fn intent(id: &str, policy: DuplicatePolicy) -> Intent {
    let mut media = MediaItem::new(PostKey::new("e621", id).unwrap());
    media.full_url = Some("https://static1.e621.net/data/test.jpg".into());
    Intent::new(
        &media,
        &FilenameTemplate::parse("{id}.{ext}").unwrap(),
        &TemplateValues {
            id,
            ext: "jpg",
            ..Default::default()
        },
        policy,
        None,
    )
    .unwrap()
}

#[test]
fn production_rejects_foreign_credentials_and_noncanonical_urls() {
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let engine = Engine::open(temp.database("db"), &root).unwrap();
    for url in [
        "http://static1.e621.net/a",
        "https://evil.test/a",
        "https://static1.e621.net.evil.test/a",
        "https://u:p@static1.e621.net/a",
        "https://static1.e621.net:444/a",
        "https://127.0.0.1/a",
        "https://static1.e621.net/a#fragment",
    ] {
        let mut value = intent("1", DuplicatePolicy::Skip);
        value.url = url.into();
        assert!(engine.enqueue(value).is_err(), "accepted {url}");
    }
    assert!(engine.jobs().unwrap().is_empty());
}

#[test]
fn grants_reject_symlinks_traversal_and_competing_owners() {
    use crate::tests::symlink;
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let db = temp.database("db");
    let engine = Engine::open(&db, &root).unwrap();
    assert!(Engine::open(&db, &root).is_err());
    symlink(&root, temp.path().join("alias")).unwrap();
    assert!(Engine::open(temp.database("db2"), temp.path().join("alias")).is_err());
    for path in [
        "../escape",
        "/absolute",
        "x/../../escape",
        ".furchive-downloads/1.part",
        "a\\b",
    ] {
        let mut value = intent("1", DuplicatePolicy::Skip);
        value.relative = path.into();
        assert!(engine.enqueue(value).is_err(), "accepted {path}");
    }
    drop(engine);
    let elsewhere = temp.path().join("elsewhere");
    std::fs::create_dir(&elsewhere).unwrap();
    assert!(
        Engine::open(&db, &elsewhere).is_err(),
        "DB silently rebound to different root"
    );
}

use tokio::io::{AsyncReadExt, AsyncWriteExt};

struct Fixture {
    origin: String,
    requests: std::sync::Arc<std::sync::Mutex<Vec<String>>>,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
async fn fixture(responses: Vec<String>) -> Fixture {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let requests = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
    let recorded = requests.clone();
    let task = tokio::spawn(async move {
        for response in responses {
            let (mut stream, _) = listener.accept().await.unwrap();
            let mut bytes = Vec::new();
            loop {
                let mut b = [0; 1024];
                let n = stream.read(&mut b).await.unwrap();
                if n == 0 {
                    break;
                }
                bytes.extend_from_slice(&b[..n]);
                if bytes.ends_with(b"\r\n\r\n") {
                    break;
                }
            }
            recorded
                .lock()
                .unwrap()
                .push(String::from_utf8(bytes).unwrap());
            let _ = stream.write_all(response.as_bytes()).await;
        }
    });
    Fixture {
        origin,
        requests,
        task,
    }
}
fn response(body: &str) -> String {
    format!(
        "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n{body}",
        body.len()
    )
}
async fn terminal(engine: &Engine, id: i64) -> Job {
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let job = engine
                .jobs()
                .unwrap()
                .into_iter()
                .find(|j| j.id == id)
                .unwrap();
            if matches!(
                job.state,
                State::Completed | State::Skipped | State::Failed | State::Cancelled
            ) {
                return job;
            }
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap()
}
#[tokio::test]
async fn worker_downloads_real_bytes_then_atomically_publishes() {
    let fixture = fixture(vec![response("real bytes")]).await;
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), &root, &fixture.origin).unwrap(),
    );
    let mut value = intent("1", DuplicatePolicy::Skip);
    value.size = Some(10);
    let id = engine.enqueue(value).unwrap();
    let worker = engine.start().unwrap();
    assert!(engine.start().is_err(), "two workers admitted");
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    assert_eq!(std::fs::read(root.join("1.jpg")).unwrap(), b"real bytes");
    assert!(!root.join(".furchive-downloads/1.part").exists());
    assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn pause_acknowledges_pending_headers_and_does_not_block_other_jobs() {
    let hanging = fixture(vec![String::new()]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &hanging.origin).unwrap(),
    );
    let first = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(
        worker.control(second, Control::Pause).await.unwrap().state,
        State::Paused
    );
    assert_eq!(
        worker.control(first, Control::Pause).await.unwrap().state,
        State::Paused
    );
    assert_eq!(
        worker.control(first, Control::Cancel).await.unwrap().state,
        State::Cancelled
    );
    assert_eq!(
        worker.control(second, Control::Cancel).await.unwrap().state,
        State::Cancelled
    );
    assert!(worker.control(9999, Control::Cancel).await.is_err());
    assert!(!temp.path().join(".furchive-downloads/1.part").exists());
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn cancelled_job_cannot_be_resumed_and_terminal_control_preserves_original() {
    let fixture = fixture(vec![response("new")]).await;
    let temp = OwnedTemp::new();
    std::fs::write(temp.path().join("1.jpg"), b"good original").unwrap();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
    );
    let id = engine
        .enqueue(intent("1", DuplicatePolicy::Overwrite))
        .unwrap();
    let worker = engine.start().unwrap();
    let cancelled = worker.control(id, Control::Cancel).await.unwrap();
    assert_eq!(cancelled.state, State::Cancelled);
    assert_eq!(
        worker.control(id, Control::Resume).await.unwrap().state,
        State::Cancelled
    );
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg")).unwrap(),
        b"good original"
    );
    worker.shutdown().await.unwrap();
}

fn seed_partial(
    engine: &Engine,
    id: i64,
    bytes: &[u8],
    committed: u64,
    validator: Option<&str>,
    total: Option<u64>,
    state: State,
) {
    use std::io::Write;
    let mut part = engine.grant.part(id).unwrap();
    part.write_all(bytes).unwrap();
    part.sync_all().unwrap();
    let mut job = engine
        .jobs()
        .unwrap()
        .into_iter()
        .find(|j| j.id == id)
        .unwrap();
    job.bytes = committed;
    job.validator = validator.map(str::to_owned);
    job.total = total;
    job.state = state;
    engine.save(&job).unwrap();
}
#[tokio::test]
async fn restart_reconciles_running_and_resumes_only_committed_bytes_with_if_range() {
    let fixture = fixture(vec!["HTTP/1.1 206 Partial Content\r\nContent-Length: 3\r\nContent-Range: bytes 3-5/6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\ndef".into()]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("db");
    let engine = Engine::open_test(&db, temp.path(), &fixture.origin).unwrap();
    let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    seed_partial(
        &engine,
        id,
        b"abcUNCOMMITTED",
        3,
        Some("etag:\"v1\""),
        Some(6),
        State::Running,
    );
    drop(engine);
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &fixture.origin).unwrap());
    assert_eq!(engine.jobs().unwrap()[0].state, State::Queued);
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"abcdef");
    let requests = fixture.requests.lock().unwrap().clone();
    assert!(requests[0].to_lowercase().contains("range: bytes=3-"));
    assert!(requests[0].to_lowercase().contains("if-range: \"v1\""));
    drop(requests);
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn valid_416_completes_only_with_matching_validator_total_and_local_size() {
    let fixture = fixture(vec!["HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nContent-Range: bytes */6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\n".into()]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
    );
    let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    seed_partial(
        &engine,
        id,
        b"abcdef",
        6,
        Some("etag:\"v1\""),
        Some(6),
        State::Queued,
    );
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"abcdef");
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn mismatched_ranges_and_validators_fail_closed_without_modifying_partial() {
    for headers in [
        "Content-Range: bytes 2-5/6\r\nETag: \"v1\"",
        "Content-Range: bytes 3-5/6\r\nETag: \"changed\"",
        "Content-Range: bytes 3-5/8\r\nETag: \"v1\"",
        "Content-Range: bytes 3-5/6",
        "Content-Range: bytes 3-4/6\r\nETag: \"v1\"",
        "Content-Range: bytes 3-5/*\r\nETag: \"v1\"",
    ] {
        let fixture = fixture(vec![format!("HTTP/1.1 206 Partial Content\r\n{headers}\r\nContent-Length: 3\r\nConnection: close\r\n\r\ndef")]).await;
        let temp = OwnedTemp::new();
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
        );
        let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        seed_partial(
            &engine,
            id,
            b"abc",
            3,
            Some("etag:\"v1\""),
            Some(6),
            State::Queued,
        );
        let worker = engine.start().unwrap();
        assert_eq!(
            terminal(&engine, id).await.state,
            State::Failed,
            "{headers}"
        );
        assert_eq!(
            std::fs::read(temp.path().join(".furchive-downloads/1.part")).unwrap(),
            b"abc"
        );
        assert!(!temp.path().join("1.jpg").exists());
        worker.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn ignored_range_200_replaces_partial_instead_of_appending() {
    let fixture = fixture(vec![response("UVWXYZ").replace("\"v1\"", "\"v2\"")]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
    );
    let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    seed_partial(
        &engine,
        id,
        b"abc",
        3,
        Some("etag:\"v1\""),
        Some(6),
        State::Queued,
    );
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"UVWXYZ");
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn hashes_and_sizes_validate_before_any_overwrite() {
    for (size, hash, succeeds) in [
        (Some(3), Some(ExpectedHash::Sha256("00".repeat(32))), false),
        (Some(9), None, false),
        (
            Some(3),
            Some(ExpectedHash::Sha256(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad".into(),
            )),
            true,
        ),
        (
            None,
            Some(ExpectedHash::Md5("900150983cd24fb0d6963f7d28e17f72".into())),
            true,
        ),
    ] {
        let fixture = fixture(vec![response("abc")]).await;
        let temp = OwnedTemp::new();
        std::fs::write(temp.path().join("1.jpg"), b"good original").unwrap();
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
        );
        let mut value = intent("1", DuplicatePolicy::Overwrite);
        value.size = size;
        value.hash = hash;
        let id = engine.enqueue(value).unwrap();
        let worker = engine.start().unwrap();
        assert_eq!(
            terminal(&engine, id).await.state,
            if succeeds {
                State::Completed
            } else {
                State::Failed
            }
        );
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg")).unwrap(),
            if succeeds {
                &b"abc"[..]
            } else {
                &b"good original"[..]
            }
        );
        worker.shutdown().await.unwrap();
    }
}
#[tokio::test]
async fn duplicate_policies_skip_overwrite_and_rename_preserve_original_until_publication() {
    for policy in [
        DuplicatePolicy::Skip,
        DuplicatePolicy::Overwrite,
        DuplicatePolicy::Rename,
    ] {
        let fixture = fixture(vec![response("new")]).await;
        let temp = OwnedTemp::new();
        std::fs::write(temp.path().join("1.jpg"), b"original").unwrap();
        std::fs::write(temp.path().join("1 (1).jpg"), b"other").unwrap();
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
        );
        let id = engine.enqueue(intent("1", policy)).unwrap();
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg")).unwrap(),
            b"original"
        );
        let worker = engine.start().unwrap();
        let job = terminal(&engine, id).await;
        assert_eq!(
            job.state,
            if policy == DuplicatePolicy::Skip {
                State::Skipped
            } else {
                State::Completed
            }
        );
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg")).unwrap(),
            if policy == DuplicatePolicy::Overwrite {
                &b"new"[..]
            } else {
                &b"original"[..]
            }
        );
        if policy == DuplicatePolicy::Rename {
            assert_eq!(job.relative_path, "1 (2).jpg");
            assert_eq!(
                std::fs::read(temp.path().join(&job.relative_path)).unwrap(),
                b"new"
            );
        }
        assert_eq!(
            std::fs::read(temp.path().join("1 (1).jpg")).unwrap(),
            b"other"
        );
        worker.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn crash_windows_before_and_after_atomic_publication_reconcile_without_redownload() {
    for crash in [1, 2, 3] {
        let fixture = fixture(vec![response("durable")]).await;
        let temp = OwnedTemp::new();
        let db = temp.database("db");
        let engine =
            std::sync::Arc::new(Engine::open_test(&db, temp.path(), &fixture.origin).unwrap());
        let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        engine
            .crash_at
            .store(crash, std::sync::atomic::Ordering::SeqCst);
        assert!(id > 0);
        let worker = engine.start().unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            loop {
                let job = engine.jobs().unwrap().remove(0);
                if job.error.is_some() {
                    assert_eq!(job.state, State::Publishing);
                    break;
                }
                tokio::time::sleep(std::time::Duration::from_millis(5)).await;
            }
        })
        .await
        .unwrap();
        worker.shutdown().await.unwrap();
        drop(engine);
        let engine = Engine::open(&db, temp.path()).unwrap();
        assert_eq!(
            engine.jobs().unwrap()[0].state,
            State::Completed,
            "crash boundary {crash}"
        );
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg")).unwrap(),
            b"durable"
        );
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn pause_mid_body_ack_freezes_bytes_and_resume_uses_real_range() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let (headers, got_headers) = tokio::sync::oneshot::channel();
    let server = tokio::spawn(async move {
        let (mut first, _) = listener.accept().await.unwrap();
        let mut request = [0; 4096];
        assert!(first.read(&mut request).await.unwrap() > 0);
        first.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\nabc").await.unwrap();
        headers.send(()).unwrap();
        let (mut second, _) = listener.accept().await.unwrap();
        let n = second.read(&mut request).await.unwrap();
        let text = String::from_utf8_lossy(&request[..n]).to_lowercase();
        assert!(text.contains("range: bytes=3-"));
        assert!(text.contains("if-range: \"v1\""));
        second.write_all(b"HTTP/1.1 206 Partial Content\r\nContent-Length: 3\r\nContent-Range: bytes 3-5/6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\ndef").await.unwrap();
    });
    let temp = OwnedTemp::new();
    let engine =
        std::sync::Arc::new(Engine::open_test(temp.database("db"), temp.path(), &origin).unwrap());
    let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    got_headers.await.unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if engine.jobs().unwrap()[0].bytes == 3 {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert_eq!(worker.control(id, Control::Pause).await.unwrap().bytes, 3);
    assert_eq!(
        std::fs::read(temp.path().join(".furchive-downloads/1.part")).unwrap(),
        b"abc"
    );
    tokio::time::sleep(std::time::Duration::from_millis(25)).await;
    assert_eq!(engine.jobs().unwrap()[0].state, State::Paused);
    assert_eq!(
        worker.control(id, Control::Resume).await.unwrap().state,
        State::Queued
    );
    assert_eq!(terminal(&engine, id).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"abcdef");
    server.await.unwrap();
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn redirects_are_not_followed_and_content_encodings_are_rejected() {
    for reply in [
        "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:9/foreign\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".to_owned(),
        "HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: 3\r\nConnection: close\r\n\r\nabc".to_owned(),
    ] {
        let fixture = fixture(vec![reply]).await;
        let temp = OwnedTemp::new();
        let engine = std::sync::Arc::new(Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap());
        let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Failed);
        assert!(!temp.path().join("1.jpg").exists());
        worker.shutdown().await.unwrap();
    }
}

#[test]
fn job_database_refuses_foreign_and_future_schemas() {
    let temp = OwnedTemp::new();
    let foreign = temp.database("foreign.db");
    let db = rusqlite::Connection::open(&foreign).unwrap();
    db.execute_batch("CREATE TABLE unrelated(value TEXT); INSERT INTO unrelated VALUES ('keep');")
        .unwrap();
    drop(db);
    assert!(Engine::open(&foreign, temp.path()).is_err());
    let future = temp.database("future.db");
    drop(Engine::open(&future, temp.path()).unwrap());
    let db = rusqlite::Connection::open(&future).unwrap();
    db.pragma_update(None, "user_version", 99).unwrap();
    drop(db);
    assert!(Engine::open(&future, temp.path()).is_err());
}

#[tokio::test]
async fn publication_target_arriving_after_queue_is_never_clobbered_by_skip_or_rename() {
    for policy in [DuplicatePolicy::Skip, DuplicatePolicy::Rename] {
        let fixture = fixture(vec![response("new")]).await;
        let temp = OwnedTemp::new();
        let db = temp.database("db");
        let engine =
            std::sync::Arc::new(Engine::open_test(&db, temp.path(), &fixture.origin).unwrap());
        engine.enqueue(intent("1", policy)).unwrap();
        engine
            .crash_at
            .store(1, std::sync::atomic::Ordering::SeqCst);
        let worker = engine.start().unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            loop {
                if engine.jobs().unwrap()[0].error.is_some() {
                    break;
                }
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
        worker.shutdown().await.unwrap();
        drop(engine);
        std::fs::write(temp.path().join("1.jpg"), b"late original").unwrap();
        let engine = Engine::open(&db, temp.path()).unwrap();
        assert_eq!(
            engine.jobs().unwrap()[0].state,
            if policy == DuplicatePolicy::Skip {
                State::Skipped
            } else {
                State::Completed
            }
        );
        assert_eq!(
            std::fs::read(temp.path().join("1.jpg")).unwrap(),
            b"late original"
        );
        if policy == DuplicatePolicy::Rename {
            assert_eq!(
                std::fs::read(temp.path().join("1 (1).jpg")).unwrap(),
                b"new"
            );
        }
    }
}

#[tokio::test]
async fn writer_rejects_symlinked_parents_leaves_and_hardlinked_parts() {
    use crate::tests::symlink;
    for mode in [0, 1, 2] {
        let fixture = fixture(vec![response("new")]).await;
        let temp = OwnedTemp::new();
        let root = temp.path().join("root");
        std::fs::create_dir(&root).unwrap();
        let outside = temp.path().join("outside");
        std::fs::create_dir(&outside).unwrap();
        std::fs::write(outside.join("original"), b"outside good").unwrap();
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("db"), &root, &fixture.origin).unwrap(),
        );
        let mut value = intent("1", DuplicatePolicy::Skip);
        if mode == 0 {
            value.relative = "nested/1.jpg".into();
        }
        let id = engine.enqueue(value).unwrap();
        if mode == 0 {
            symlink(&outside, root.join("nested")).unwrap();
        }
        if mode == 1 {
            symlink(outside.join("original"), root.join("1.jpg")).unwrap();
        }
        if mode == 2 {
            std::fs::hard_link(
                outside.join("original"),
                root.join(".furchive-downloads/1.part"),
            )
            .unwrap();
        }
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Failed);
        assert_eq!(
            std::fs::read(outside.join("original")).unwrap(),
            b"outside good"
        );
        assert!(!outside.join("1.jpg").exists());
        worker.shutdown().await.unwrap();
    }
}

#[test]
fn validators_reject_weak_tags_and_unproven_dates() {
    use reqwest::header::{HeaderMap, HeaderValue, DATE, ETAG, LAST_MODIFIED};
    let mut headers = HeaderMap::new();
    headers.insert(ETAG, HeaderValue::from_static("W/\"weak\""));
    headers.insert(LAST_MODIFIED, HeaderValue::from_static("not-a-date"));
    assert_eq!(crate::worker::validator(&headers), None);
    headers.insert(
        LAST_MODIFIED,
        HeaderValue::from_static("Wed, 21 Oct 2015 07:28:00 GMT"),
    );
    assert_eq!(
        crate::worker::validator(&headers),
        None,
        "Last-Modified without strong Date proof"
    );
    headers.remove(ETAG);
    headers.insert(
        DATE,
        HeaderValue::from_static("Wed, 21 Oct 2015 07:29:00 GMT"),
    );
    assert_eq!(
        crate::worker::validator(&headers),
        Some("date:Wed, 21 Oct 2015 07:28:00 GMT".into())
    );
    headers.insert(ETAG, HeaderValue::from_static("\"embedded\"quote\""));
    assert_ne!(
        crate::worker::validator(&headers),
        Some("etag:\"embedded\"quote\"".into())
    );
}

#[tokio::test]
async fn last_modified_resume_uses_date_if_range_and_unknown_validator_restarts() {
    for use_date in [true, false] {
        let reply = if use_date {
            "HTTP/1.1 206 Partial Content\r\nContent-Length: 3\r\nContent-Range: bytes 3-5/6\r\nLast-Modified: Wed, 21 Oct 2015 07:28:00 GMT\r\nDate: Wed, 21 Oct 2015 07:30:00 GMT\r\nConnection: close\r\n\r\ndef".into()
        } else {
            response("abcdef")
        };
        let fixture = fixture(vec![reply]).await;
        let temp = OwnedTemp::new();
        let engine = std::sync::Arc::new(
            Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
        );
        let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        seed_partial(
            &engine,
            id,
            b"abc",
            3,
            if use_date {
                Some("date:Wed, 21 Oct 2015 07:28:00 GMT")
            } else {
                None
            },
            Some(6),
            State::Queued,
        );
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Completed);
        assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"abcdef");
        let requests = fixture.requests.lock().unwrap().clone();
        assert_eq!(
            requests[0].to_lowercase().contains("range: bytes=3-"),
            use_date
        );
        if use_date {
            assert!(requests[0].contains("if-range: Wed, 21 Oct 2015 07:28:00 GMT"));
        }
        drop(requests);
        worker.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn invalid_416_and_short_bodies_never_publish() {
    for reply in [
        "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */6\r\nETag: \"changed\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        "HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */7\r\nETag: \"v1\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 3-5/6\r\nETag: \"v1\"\r\nContent-Length: 3\r\nConnection: close\r\n\r\nd",
    ] {
        let fixture = fixture(vec![reply.into()]).await;
        let temp = OwnedTemp::new();
        let engine = std::sync::Arc::new(Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap());
        let id = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        seed_partial(&engine, id, b"abc", 3, Some("etag:\"v1\""), Some(6), State::Queued);
        let worker = engine.start().unwrap();
        assert_eq!(terminal(&engine, id).await.state, State::Failed);
        assert!(!temp.path().join("1.jpg").exists());
        worker.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn process_crash_child() {
    let Ok(base) = std::env::var("FURCHIVE_CRASH_CHILD_ROOT") else {
        return;
    };
    let origin = std::env::var("FURCHIVE_CRASH_CHILD_ORIGIN").unwrap();
    let root = std::path::Path::new(&base);
    let engine = std::sync::Arc::new(
        Engine::open_test(root.parent().unwrap().join("state/db"), root, &origin).unwrap(),
    );
    engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    let _worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if engine.jobs().unwrap()[0].bytes == 3 {
                std::process::exit(77);
            }
            tokio::time::sleep(std::time::Duration::from_millis(2)).await;
        }
    })
    .await
    .unwrap();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn real_process_death_releases_owner_and_resumes_durable_partial() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let server = tokio::spawn(async move {
        let (mut first, _) = listener.accept().await.unwrap();
        let mut request = [0; 4096];
        assert!(first.read(&mut request).await.unwrap() > 0);
        first.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\nabc").await.unwrap();
        let (mut second, _) = listener.accept().await.unwrap();
        let n = second.read(&mut request).await.unwrap();
        assert!(String::from_utf8_lossy(&request[..n])
            .to_lowercase()
            .contains("range: bytes=3-"));
        second.write_all(b"HTTP/1.1 206 Partial Content\r\nContent-Length: 3\r\nContent-Range: bytes 3-5/6\r\nETag: \"v1\"\r\nConnection: close\r\n\r\ndef").await.unwrap();
    });
    let temp = OwnedTemp::new();
    let mut child = std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "tests::process_crash_child", "--nocapture"])
        .env("FURCHIVE_CRASH_CHILD_ROOT", temp.path())
        .env("FURCHIVE_CRASH_CHILD_ORIGIN", &origin)
        // The parent checks exit77 and durable DB/bytes. Child harness banners
        // must not splice the parent's named-test success line in native gates.
        .stdout(std::process::Stdio::null())
        .spawn()
        .unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(8), async {
        loop {
            if let Some(status) = child.try_wait().unwrap() {
                assert_eq!(status.code(), Some(77));
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap_or_else(|e| {
        let _ = child.kill();
        panic!("child timeout {e}")
    });
    let engine =
        std::sync::Arc::new(Engine::open_test(temp.database("db"), temp.path(), &origin).unwrap());
    let job = engine.jobs().unwrap().remove(0);
    assert_eq!(job.state, State::Queued);
    assert_eq!(job.bytes, 3);
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, job.id).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("1.jpg")).unwrap(), b"abcdef");
    server.await.unwrap();
    assert_eq!(
        worker.control(job.id, Control::Cancel).await.unwrap().state,
        State::Completed
    );
    worker.shutdown().await.unwrap();
}

#[test]
fn persisted_jobs_reject_malformed_identity_paths_and_validators_before_worker() {
    for mutation in ["id", "key", "path", "validator", "bytes", "hash"] {
        let temp = OwnedTemp::new();
        let db = temp.database("db");
        let engine = Engine::open(&db, temp.path()).unwrap();
        engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
        drop(engine);
        let connection = rusqlite::Connection::open(&db).unwrap();
        let text: String = connection
            .query_row("SELECT payload FROM jobs", [], |r| r.get(0))
            .unwrap();
        let mut value: serde_json::Value = serde_json::from_str(&text).unwrap();
        match mutation {
            "id" => value["id"] = 999.into(),
            "key" => {
                value["key"] = serde_json::to_value(PostKey::new("other", "2").unwrap()).unwrap()
            }
            "path" => value["relative_path"] = "../outside".into(),
            "validator" => value["validator"] = "missing colon".into(),
            "bytes" => value["bytes"] = u64::MAX.into(),
            "hash" => value["intent"]["hash"] = serde_json::json!({"Sha256":"bad"}),
            _ => unreachable!(),
        }
        connection
            .execute("UPDATE jobs SET payload=?", [value.to_string()])
            .unwrap();
        drop(connection);
        assert!(
            Engine::open(&db, temp.path()).is_err(),
            "accepted corrupted {mutation}"
        );
    }
}

#[tokio::test]
async fn cancel_during_stalled_headers_acknowledges_and_next_job_runs() {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}", listener.local_addr().unwrap());
    let (seen, seen_request) = tokio::sync::oneshot::channel();
    let server = tokio::spawn(async move {
        let (mut stalled, _) = listener.accept().await.unwrap();
        let mut buf = [0; 4096];
        assert!(stalled.read(&mut buf).await.unwrap() > 0);
        seen.send(()).unwrap();
        let (mut next, _) = listener.accept().await.unwrap();
        assert!(next.read(&mut buf).await.unwrap() > 0);
        next.write_all(response("next").as_bytes()).await.unwrap();
    });
    let temp = OwnedTemp::new();
    std::fs::write(temp.path().join("1.jpg"), b"original").unwrap();
    let engine =
        std::sync::Arc::new(Engine::open_test(temp.database("db"), temp.path(), &origin).unwrap());
    let first = engine
        .enqueue(intent("1", DuplicatePolicy::Overwrite))
        .unwrap();
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    seen_request.await.unwrap();
    let receipt = tokio::time::timeout(
        std::time::Duration::from_secs(1),
        worker.control(first, Control::Cancel),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(receipt.state, State::Cancelled);
    assert!(!temp.path().join(".furchive-downloads/1.part").exists());
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg")).unwrap(),
        b"original"
    );
    assert_eq!(terminal(&engine, second).await.state, State::Completed);
    assert_eq!(std::fs::read(temp.path().join("2.jpg")).unwrap(), b"next");
    server.await.unwrap();
    worker.shutdown().await.unwrap();
}

#[tokio::test]
async fn concurrent_enqueues_run_in_persisted_id_order_without_duplicate_writers() {
    let fixture = fixture((0..12).map(|_| response("same")).collect()).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
    );
    let mut enqueues = Vec::new();
    for index in 0..12 {
        let engine = engine.clone();
        enqueues.push(tokio::task::spawn_blocking(move || {
            let mut value = intent(&index.to_string(), DuplicatePolicy::Rename);
            value.url = format!("https://static1.e621.net/{index}");
            value.relative = "same.jpg".into();
            engine.enqueue(value).unwrap()
        }));
    }
    for task in enqueues {
        task.await.unwrap();
    }
    let expected: Vec<_> = engine
        .jobs()
        .unwrap()
        .iter()
        .map(|j| url::Url::parse(&j.intent.url).unwrap().path().to_owned())
        .collect();
    let worker = engine.start().unwrap();
    let jobs = engine.jobs().unwrap();
    for job in &jobs {
        assert_eq!(terminal(&engine, job.id).await.state, State::Completed);
    }
    let requests = fixture.requests.lock().unwrap().clone();
    let actual: Vec<_> = requests
        .iter()
        .map(|r| r.split_whitespace().nth(1).unwrap().to_owned())
        .collect();
    assert_eq!(actual, expected);
    let paths: std::collections::HashSet<_> = engine
        .jobs()
        .unwrap()
        .into_iter()
        .map(|j| j.relative_path)
        .collect();
    assert_eq!(paths.len(), 12);
    for path in paths {
        assert_eq!(std::fs::read(temp.path().join(path)).unwrap(), b"same");
    }
    worker.shutdown().await.unwrap();
}

#[test]
fn different_job_databases_cannot_reuse_one_roots_staging_namespace() {
    let temp = OwnedTemp::new();
    let root = temp.path().join("root");
    std::fs::create_dir(&root).unwrap();
    let first_db = temp.database("one.db");
    let first = Engine::open(&first_db, &root).unwrap();
    first.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    seed_partial(
        &first,
        1,
        b"precious partial",
        16,
        Some("etag:\"v1\""),
        None,
        State::Paused,
    );
    drop(first);
    assert!(Engine::open(temp.database("other.db"), &root).is_err());
    assert_eq!(
        std::fs::read(root.join(".furchive-downloads/1.part")).unwrap(),
        b"precious partial"
    );
    assert!(Engine::open(&first_db, &root).is_ok());
}

#[test]
fn deserialize_cannot_bypass_portable_name_limits() {
    let temp = OwnedTemp::new();
    let engine = Engine::open(temp.database("db"), temp.path()).unwrap();
    for path in [
        "CON.jpg".to_owned(),
        "trailing. ".to_owned(),
        "a?b.jpg".to_owned(),
        "x".repeat(256),
    ] {
        let mut value = intent("1", DuplicatePolicy::Skip);
        value.relative = path.clone();
        assert!(
            engine.enqueue(value).is_err(),
            "accepted nonportable {path}"
        );
    }
}

#[tokio::test]
#[cfg(target_os = "linux")]
async fn destination_cannot_overwrite_engine_database_or_sqlite_companions() {
    let temp = OwnedTemp::new();
    let root = temp.path();
    let db = root.join("jobs.db");
    let engine = Engine::open(&db, root).unwrap();
    for path in ["jobs.db", "jobs.db-wal", "jobs.db-shm", "jobs.db-journal"] {
        let mut value = intent("1", DuplicatePolicy::Overwrite);
        value.relative = path.into();
        assert!(
            engine.enqueue(value).is_err(),
            "admitted database clobber {path}"
        );
    }
    assert!(engine.jobs().unwrap().is_empty());
}

#[tokio::test]
async fn cannot_start_worker_without_runtime_and_can_restart_after_acknowledged_shutdown() {
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(Engine::open(temp.database("db"), temp.path()).unwrap());
    let outside = engine.clone();
    std::thread::spawn(move || {
        assert!(outside.start().is_err());
    })
    .join()
    .unwrap();
    let first = engine.start().unwrap();
    first.shutdown().await.unwrap();
    let next = engine.start().unwrap();
    next.shutdown().await.unwrap();
}

#[tokio::test]
async fn impossible_http_totals_fail_job_without_poisoning_queue() {
    let fixture = fixture(vec![
        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 3-18446744073709551614/18446744073709551615\r\nETag: \"v1\"\r\nConnection: close\r\n\r\ndef".into(),
        response("next"),
    ]).await;
    let temp = OwnedTemp::new();
    let engine = std::sync::Arc::new(
        Engine::open_test(temp.database("db"), temp.path(), &fixture.origin).unwrap(),
    );
    let first = engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    seed_partial(
        &engine,
        first,
        b"abc",
        3,
        Some("etag:\"v1\""),
        None,
        State::Queued,
    );
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    let worker = engine.start().unwrap();
    assert_eq!(terminal(&engine, first).await.state, State::Failed);
    assert_eq!(terminal(&engine, second).await.state, State::Completed);
    worker.shutdown().await.unwrap();
}

#[test]
fn durable_fifo_retains_core_identity_without_touching_originals() {
    let temp = OwnedTemp::new();
    let root = temp.path().join("media");
    std::fs::create_dir(&root).unwrap();
    std::fs::write(root.join("1.jpg"), b"good original").unwrap();
    let db = temp.database("jobs.db");
    let engine = Engine::open(&db, &root).unwrap();
    let first = engine
        .enqueue(intent("1", DuplicatePolicy::Overwrite))
        .unwrap();
    let second = engine.enqueue(intent("2", DuplicatePolicy::Skip)).unwrap();
    assert!(first < second);
    assert_eq!(std::fs::read(root.join("1.jpg")).unwrap(), b"good original");
    drop(engine);
    let restarted = Engine::open(&db, &root).unwrap();
    let jobs = restarted.jobs().unwrap();
    assert_eq!(
        jobs.iter().map(|j| j.id).collect::<Vec<_>>(),
        vec![first, second]
    );
    assert_eq!(jobs[0].key, PostKey::new("e621", "1").unwrap());
    assert_eq!(jobs[0].state, State::Queued);
}
