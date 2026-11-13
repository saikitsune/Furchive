use furchive_app::{downloads::Downloads, settings::Settings};
use furchive_domain::{MediaItem, PostKey};
fn owned_temp() -> tempfile::TempDir {
    tempfile::tempdir_in(std::fs::canonicalize(std::env::temp_dir()).unwrap()).unwrap()
}
#[cfg(feature = "fixture-downloads")]
#[tokio::test]
async fn upstream_hash_mismatch_never_overwrites_good_download() {
    use std::io::{Read, Write};
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir_all(root.join("e621/_")).unwrap();
    std::fs::write(root.join("e621/_/1.png"), b"existing artwork").unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let server = std::thread::spawn(move || {
        let (mut s, _) = listener.accept().unwrap();
        let mut b = [0; 4096];
        let n = s.read(&mut b).unwrap();
        assert!(n > 0);
        s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nwrng")
            .unwrap();
    });
    let hub = Downloads::fixture(dir.path().join("queue.sqlite"), origin).unwrap();
    hub.select_root(&root).await.unwrap();
    let mut p = post();
    p.file_md5 = Some("00000000000000000000000000000000".into());
    let settings = Settings {
        duplicates: furchive_app::settings::DuplicatePolicy::Overwrite,
        ..Default::default()
    };
    let id = hub.enqueue(&p, &settings).await.unwrap();
    hub.control(id, "resume").await.unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if hub.jobs().await.unwrap()[0].state == "Failed" {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    hub.shutdown().await.unwrap();
    server.join().unwrap();
    assert_eq!(
        std::fs::read(root.join("e621/_/1.png")).unwrap(),
        b"existing artwork"
    );
}
#[tokio::test]
async fn pool_group_uses_authoritative_page_numbers_and_persists_membership() {
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    hub.select_root(&root).await.unwrap();
    let mut pool = furchive_domain::Pool::new(
        furchive_domain::PoolKey::new("e621", "3").unwrap(),
        "Fixture pool".into(),
    );
    pool.post_ids = vec!["1".into(), "2".into(), "3".into()];
    let mut p3 = post();
    p3.key = furchive_domain::PostKey::new("e621", "3").unwrap();
    let page = furchive_app::browse::assemble_pool_page(
        pool,
        vec![p3, post()],
        0,
        3,
        &furchive_domain::RatingFilter::Any,
    )
    .unwrap();
    let group = hub
        .enqueue_pool_page(&page, &Settings::default())
        .await
        .unwrap();
    let jobs = hub.jobs().await.unwrap();
    assert_eq!(jobs.len(), 2);
    assert!(
        jobs.iter()
            .all(|j| j.group_id.as_deref() == Some(group.as_str()))
    );
    assert_eq!(
        jobs.iter().map(|j| j.group_position).collect::<Vec<_>>(),
        vec![Some(0), Some(2)]
    );
    assert!(jobs[0].relative_path.ends_with("1_1.png"));
    assert!(jobs[1].relative_path.ends_with("3_3.png"));
    hub.control_group(&group, "cancel").await.unwrap();
    assert!(
        hub.jobs()
            .await
            .unwrap()
            .iter()
            .all(|j| j.state == "Cancelled")
    );
    hub.shutdown().await.unwrap();
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    hub.select_root(&root).await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[1].group_position, Some(2));
    hub.shutdown().await.unwrap();
}
#[cfg(feature = "fixture-downloads")]
#[tokio::test]
async fn service_uses_granted_concurrency_for_two_independent_transfers() {
    use std::io::{Read, Write};
    use std::sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    };
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let seen = Arc::new(AtomicUsize::new(0));
    let count = seen.clone();
    let server = std::thread::spawn(move || {
        let mut threads = vec![];
        for _ in 0..2 {
            let (mut s, _) = listener.accept().unwrap();
            let count = count.clone();
            threads.push(std::thread::spawn(move || {
                let mut b = [0; 4096];
                let n = s.read(&mut b).unwrap();
                assert!(n > 0);
                s.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\n")
                    .unwrap();
                count.fetch_add(1, Ordering::SeqCst);
                let end = std::time::Instant::now() + std::time::Duration::from_secs(3);
                while count.load(Ordering::SeqCst) < 2 {
                    assert!(std::time::Instant::now() < end);
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
                s.write_all(b"data").unwrap();
            }));
        }
        for t in threads {
            t.join().unwrap();
        }
    });
    let hub = Downloads::fixture(dir.path().join("queue.sqlite"), origin).unwrap();
    hub.select_root_with_concurrency(&root, 2).await.unwrap();
    let settings = Settings {
        filename_template: "{source}/{id}/{id}.{ext}".into(),
        ..Default::default()
    };
    for id in ["1", "2"] {
        let mut p = post();
        p.key = PostKey::new("e621", id).unwrap();
        let job = hub.enqueue(&p, &settings).await.unwrap();
        hub.control(job, "resume").await.unwrap();
    }
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        while hub
            .jobs()
            .await
            .unwrap()
            .iter()
            .any(|j| j.state != "Completed")
        {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(seen.load(Ordering::SeqCst), 2);
    hub.shutdown().await.unwrap();
    server.join().unwrap();
    assert_eq!(std::fs::read(root.join("e621/1/1.png")).unwrap(), b"data");
    assert_eq!(std::fs::read(root.join("e621/2/2.png")).unwrap(), b"data");
}
fn post() -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", "1").unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.file_extension = Some("png".into());
    p.rating = furchive_domain::Rating::Safe;
    p
}

#[tokio::test]
async fn metadata_opt_in_snapshots_single_page_and_whole_pool_only_at_new_admission() {
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    hub.select_root(&root).await.unwrap();
    hub.enqueue(&post(), &Settings::default()).await.unwrap();
    let settings = Settings {
        save_metadata: true,
        ..Default::default()
    };
    let mut p = post();
    p.title = "At admission".into();
    hub.enqueue(&p, &settings).await.unwrap();
    let mut pool = furchive_domain::Pool::new(
        furchive_domain::PoolKey::new("e621", "3").unwrap(),
        "Fixture pool".into(),
    );
    pool.post_ids = vec!["1".into()];
    let page = furchive_app::browse::assemble_pool_page(
        pool,
        vec![p.clone()],
        0,
        1,
        &furchive_domain::RatingFilter::Any,
    )
    .unwrap();
    hub.enqueue_pool_page(&page, &settings).await.unwrap();
    hub.enqueue_whole_pool(&page, &settings).await.unwrap();
    p.title = "Changed after admission".into();
    let jobs = hub.jobs().await.unwrap();
    assert_eq!(jobs[0].metadata_status, "NotRequested");
    assert!(jobs[1..].iter().all(|j| j.metadata_status == "Pending"
        && j.state == "Paused"
        && !j.metadata_retry_available));
    hub.shutdown().await.unwrap();
    let db = rusqlite::Connection::open(dir.path().join("queue.sqlite")).unwrap();
    let text: String = db
        .query_row("SELECT payload FROM jobs WHERE id=2", [], |r| r.get(0))
        .unwrap();
    let json: serde_json::Value = serde_json::from_str(&text).unwrap();
    assert_eq!(json["intent"]["metadata"]["title"], "At admission");
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    hub.select_root(&root).await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[0].metadata_status, "NotRequested");
    assert!(hub.control(2, "retry_metadata").await.is_err());
    hub.shutdown().await.unwrap();
}
#[tokio::test]
async fn download_service_requires_explicit_root_and_acknowledges_persisted_controls() {
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    assert!(hub.enqueue(&post(), &Settings::default()).await.is_err());
    hub.select_root(&root).await.unwrap();
    let id = hub.enqueue(&post(), &Settings::default()).await.unwrap();
    let jobs = hub.jobs().await.unwrap();
    assert_eq!(jobs[0].id, id);
    assert!(jobs[0].relative_path.ends_with("1.png"));
    hub.control(id, "pause").await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[0].state, "Paused");
    hub.control(id, "cancel").await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[0].state, "Cancelled");
    hub.shutdown().await.unwrap();
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    assert!(hub.jobs().await.unwrap().is_empty());
    hub.select_root(&root).await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[0].state, "Cancelled");
    hub.shutdown().await.unwrap();
}
#[cfg(feature = "fixture-downloads")]
#[tokio::test]
async fn download_service_commits_exact_http_fixture_and_reopens_history() {
    use std::io::{Read, Write};
    let dir = owned_temp();
    let root = dir.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let body = b"owned HTTP artifact";
    let server = std::thread::spawn(move || {
        let (mut s, _) = listener.accept().unwrap();
        s.set_read_timeout(Some(std::time::Duration::from_secs(4)))
            .unwrap();
        let mut b = [0; 4096];
        let n = s.read(&mut b).unwrap();
        assert!(n > 0);
        write!(s,"HTTP/1.1 200 OK\r\nContent-Length: {}\r\nETag: \"owned-v1\"\r\nConnection: close\r\n\r\n",body.len()).unwrap();
        s.write_all(body).unwrap();
    });
    let hub = Downloads::fixture(dir.path().join("queue.sqlite"), origin).unwrap();
    hub.select_root(&root).await.unwrap();
    let mut p = post();
    p.file_size_bytes = Some(body.len() as u64);
    let id = hub.enqueue(&p, &Settings::default()).await.unwrap();
    hub.control(id, "resume").await.unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if hub.jobs().await.unwrap()[0].state == "Completed" {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    hub.shutdown().await.unwrap();
    server.join().unwrap();
    assert_eq!(std::fs::read(root.join("e621/_/1.png")).unwrap(), body);
    let hub = Downloads::new(dir.path().join("queue.sqlite"));
    hub.select_root(&root).await.unwrap();
    assert_eq!(hub.jobs().await.unwrap()[0].state, "Completed");
    hub.shutdown().await.unwrap();
}
