//! Deterministic NYX-LM-01 regression: occupy the actual blocking reader pool,
//! poll the real request to Pending, revoke authority, then release the reader.
#![cfg(all(feature = "fixture-downloads", feature = "fixture-media"))]
use furchive_app::{
    downloads::Downloads,
    media_cache::{MediaCache, Variant},
    settings::Settings,
};
use furchive_domain::{MediaItem, PostKey};
use std::{
    io::{Read, Write},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    time::Duration,
};
const A: &str = "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png";
const B: &str = "https://static1.e621.net/data/aa/bb/aabb1111111111111111111111111111.png";
const PNG: &[u8] = include_bytes!("../../../apps/desktop/public/fixtures/still.png");
fn post(url: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", "100").unwrap());
    p.full_url = Some(url.into());
    p.file_extension = Some("png".into());
    p
}
struct Server {
    origin: String,
    count: Arc<AtomicUsize>,
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
}
impl Server {
    fn new() -> Self {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let origin = format!("http://{}/", listener.local_addr().unwrap());
        let count = Arc::new(AtomicUsize::new(0));
        let stop = Arc::new(AtomicBool::new(false));
        let (c, s) = (count.clone(), stop.clone());
        let thread = std::thread::spawn(move || {
            while !s.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok((mut socket, _)) => {
                        socket
                            .set_read_timeout(Some(Duration::from_secs(2)))
                            .unwrap();
                        let mut b = [0; 8192];
                        if socket.read(&mut b).unwrap_or(0) > 0 {
                            c.fetch_add(1, Ordering::SeqCst);
                            let _ = write!(
                                socket,
                                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                                PNG.len()
                            );
                            let _ = socket.write_all(PNG);
                        }
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
            count,
            stop,
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
fn runtime() -> tokio::runtime::Runtime {
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .max_blocking_threads(1)
        .enable_all()
        .build()
        .unwrap()
}
async fn stale(local_hit: bool, damage: bool) {
    let base = std::env::temp_dir().canonicalize().unwrap();
    let temp = tempfile::tempdir_in(base).unwrap();
    let art = temp.path().join("art");
    std::fs::create_dir(&art).unwrap();
    let server = Server::new();
    let downloads =
        Downloads::fixture(temp.path().join("q.sqlite"), server.origin.clone()).unwrap();
    downloads.select_root(&art).await.unwrap();
    let settings = Settings {
        filename_template: "{id}.{ext}".into(),
        ..Default::default()
    };
    let id = downloads.enqueue(&post(A), &settings).await.unwrap();
    downloads.control(id, "resume").await.unwrap();
    tokio::time::timeout(Duration::from_secs(3), async {
        while downloads.jobs().await.unwrap()[0].state != "Completed" {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    let cache = MediaCache::local_fixture(1024 * 1024, &server.origin).unwrap();
    cache
        .authorize(&[post(if local_hit { A } else { B })])
        .unwrap();
    let before = server.count.load(Ordering::SeqCst);
    let (entered_tx, entered_rx) = std::sync::mpsc::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let blocker = tokio::task::spawn_blocking(move || {
        entered_tx.send(()).unwrap();
        release_rx.recv_timeout(Duration::from_secs(5)).unwrap();
    });
    entered_rx.recv_timeout(Duration::from_secs(2)).unwrap();
    let mut request = Box::pin(cache.prepare_with_downloads("100", Variant::Full, &downloads));
    let mut cx = std::task::Context::from_waker(std::task::Waker::noop());
    assert!(matches!(
        request.as_mut().poll(&mut cx),
        std::task::Poll::Pending
    ));
    cache.authorize(&[post(A)]).unwrap();
    if damage {
        std::fs::write(art.join("100.png"), b"damaged current publication").unwrap();
    }
    release_tx.send(()).unwrap();
    let result = tokio::time::timeout(Duration::from_secs(3), request)
        .await
        .unwrap();
    blocker.await.unwrap();
    downloads.shutdown().await.unwrap();
    assert!(
        result.is_err(),
        "stale request must retain captured generation instead of minting new authority: {result:?}"
    );
    assert_eq!(
        server.count.load(Ordering::SeqCst),
        before,
        "stale local miss must not start new HTTP work"
    );
}
#[test]
fn delayed_local_miss_cannot_renew_grant() {
    runtime().block_on(stale(false, false));
}
#[test]
fn delayed_local_miss_cannot_bypass_new_publication_corruption() {
    runtime().block_on(stale(false, true));
}
#[test]
fn delayed_local_hit_stays_revoked() {
    runtime().block_on(stale(true, false));
}
#[tokio::test]
async fn current_local_miss_still_uses_captured_network_resource() {
    let server = Server::new();
    let base = std::env::temp_dir().canonicalize().unwrap();
    let temp = tempfile::tempdir_in(base).unwrap();
    let art = temp.path().join("art");
    std::fs::create_dir(&art).unwrap();
    let downloads =
        Downloads::fixture(temp.path().join("q.sqlite"), server.origin.clone()).unwrap();
    downloads.select_root(&art).await.unwrap();
    let cache = MediaCache::local_fixture(1024 * 1024, &server.origin).unwrap();
    cache.authorize(&[post(B)]).unwrap();
    let h = cache
        .prepare_with_downloads("100", Variant::Full, &downloads)
        .await
        .unwrap();
    assert_eq!(cache.get(&h).unwrap().bytes.as_slice(), PNG);
    assert_eq!(server.count.load(Ordering::SeqCst), 1);
    downloads.shutdown().await.unwrap();
}
