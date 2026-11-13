//! Owner composition test: real filtered HTTP metadata -> Rust edge cursor ->
//! paused Rename download+normalized sidecar -> restart -> local full-media read.
#![cfg(all(feature = "fixture-downloads", feature = "fixture-media"))]
use furchive_app::{
    Database,
    browse::Browser,
    downloads::Downloads,
    media_cache::{MediaCache, Variant},
    settings::{DuplicatePolicy, Settings},
    viewer_cursor::{Intent, ViewerCursor},
};
use furchive_domain::{MediaItem, PostKey, SearchQuery};
use std::{
    io::{Read, Write},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::Duration,
};
const URL: &str = "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png";
const PNG: &[u8] = include_bytes!("../../../apps/desktop/public/fixtures/still.png");
struct Server {
    origin: String,
    stop: Arc<AtomicBool>,
    media: Arc<AtomicUsize>,
    thread: Option<std::thread::JoinHandle<()>>,
}
impl Server {
    fn new() -> Self {
        let l = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        l.set_nonblocking(true).unwrap();
        let origin = format!("http://{}/", l.local_addr().unwrap());
        let stop = Arc::new(AtomicBool::new(false));
        let media = Arc::new(AtomicUsize::new(0));
        let (s, m) = (stop.clone(), media.clone());
        let thread = std::thread::spawn(move || {
            while !s.load(Ordering::SeqCst) {
                match l.accept() {
                    Ok((mut socket, _)) => {
                        socket
                            .set_read_timeout(Some(Duration::from_secs(2)))
                            .unwrap();
                        let mut buf = [0; 8192];
                        let n = socket.read(&mut buf).unwrap_or(0);
                        if n == 0 {
                            continue;
                        }
                        let request = String::from_utf8_lossy(&buf[..n]);
                        assert!(!request.to_lowercase().contains("authorization:"));
                        let bytes = if request.starts_with("GET /posts.json") {
                            assert!(request.contains("page=2"));
                            assert!(!request.contains("filter_id"));
                            serde_json::to_vec(&serde_json::json!({"posts":[{"id":200,"rating":"s","tags":{"artist":["fixture"]},"description":"Harmless next-page fixture","file":{"url":URL,"ext":"png","size":PNG.len()},"preview":{"url":URL},"score":{"total":1}}]})).unwrap()
                        } else {
                            assert!(request.starts_with("GET /data/aa/bb/"));
                            m.fetch_add(1, Ordering::SeqCst);
                            PNG.to_vec()
                        };
                        let _ = write!(
                            socket,
                            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                            bytes.len()
                        );
                        let _ = socket.write_all(&bytes);
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                        std::thread::sleep(Duration::from_millis(2))
                    }
                    Err(e) => panic!("{e}"),
                }
            }
        });
        Self {
            origin,
            stop,
            media,
            thread: Some(thread),
        }
    }
}
impl Drop for Server {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        self.thread.take().unwrap().join().unwrap();
    }
}
#[tokio::test]
async fn next_page_download_sidecar_and_offline_local_read_preserve_original_after_restart() {
    let base = std::env::temp_dir().canonicalize().unwrap();
    let temp = tempfile::tempdir_in(base).unwrap();
    let art = temp.path().join("art");
    std::fs::create_dir(&art).unwrap();
    std::fs::write(art.join("200.png"), b"existing owned art").unwrap();
    let server = Server::new();
    let db = Database::open(&temp.path().join("profile")).await.unwrap();
    let browser = Browser::new(
        furchive_e621::E621Client::local_fixture(&server.origin).unwrap(),
        db.clone(),
    );
    let cache = MediaCache::local_fixture(1024 * 1024, &server.origin).unwrap();
    let mut first = MediaItem::new(PostKey::new("e621", "100").unwrap());
    first.full_url = Some(URL.into());
    cache.authorize(&[first]).unwrap();
    let cursor = ViewerCursor::default();
    cursor
        .search(
            cache.generation().unwrap(),
            Intent::Search(SearchQuery::default()),
            1,
            Some(2),
            vec!["100".into()],
        )
        .unwrap();
    let next = cursor
        .navigate(&browser, &cache, "100", 1)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(next.selected_id, "200");
    assert_eq!(next.viewable_ids, vec!["200"]);
    assert!(cache.authorized_post("100").is_err());
    let post = cache.authorized_post("200").unwrap();
    let queue = temp.path().join("queue.sqlite");
    let downloads = Downloads::fixture(queue.clone(), server.origin.clone()).unwrap();
    downloads.select_root(&art).await.unwrap();
    let settings = Settings {
        save_metadata: true,
        duplicates: DuplicatePolicy::Rename,
        filename_template: "{id}.{ext}".into(),
        ..Default::default()
    };
    let id = downloads.enqueue(&post, &settings).await.unwrap();
    assert_eq!(downloads.jobs().await.unwrap()[0].state, "Paused");
    downloads.control(id, "resume").await.unwrap();
    tokio::time::timeout(Duration::from_secs(4), async {
        loop {
            let jobs = downloads.jobs().await.unwrap();
            if jobs[0].state == "Completed" && jobs[0].metadata_status == "Written" {
                break;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    downloads.shutdown().await.unwrap();
    assert_eq!(server.media.load(Ordering::SeqCst), 1);
    let metadata = art.join("200 (1).png.metadata.json");
    let snapshot = std::fs::read(&metadata).unwrap();
    let json: serde_json::Value = serde_json::from_slice(&snapshot).unwrap();
    assert_eq!(json["representation"], "normalized-furchive-v1");
    assert_eq!(json["key"]["id"], "200");
    assert_eq!(json["url"], URL);
    let origin = server.origin.clone();
    drop(server);
    let reopened = Downloads::new(queue);
    reopened.select_root(&art).await.unwrap();
    assert_eq!(reopened.jobs().await.unwrap()[0].metadata_status, "Written");
    let cache = MediaCache::local_fixture(1024 * 1024, &origin).unwrap();
    cache.authorize(&[post]).unwrap();
    let handle = cache
        .prepare_with_downloads("200", Variant::Full, &reopened)
        .await
        .unwrap();
    assert_eq!(cache.get(&handle).unwrap().bytes.as_slice(), PNG);
    assert_eq!(
        std::fs::read(art.join("200.png")).unwrap(),
        b"existing owned art"
    );
    assert_eq!(std::fs::read(metadata).unwrap(), snapshot);
    reopened.shutdown().await.unwrap();
    db.shutdown().await.unwrap();
}
