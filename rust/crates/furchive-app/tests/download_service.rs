use furchive_app::{downloads::Downloads, settings::Settings};
use furchive_domain::{MediaItem, PostKey};
fn post() -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", "1").unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.file_extension = Some("png".into());
    p
}
#[tokio::test]
async fn download_service_requires_explicit_root_and_acknowledges_persisted_controls() {
    let dir = tempfile::tempdir().unwrap();
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
    let dir = tempfile::tempdir().unwrap();
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
