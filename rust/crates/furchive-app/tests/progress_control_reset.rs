#![cfg(feature = "fixture-downloads")]
use furchive_domain::{MediaItem, PostKey};
use std::{
    sync::{Arc, Mutex},
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    sync::watch,
};
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.file_extension = Some("bin".into());
    p.file_size_bytes = Some(6);
    p
}
struct Local {
    origin: String,
    release: watch::Sender<bool>,
    join: tokio::task::JoinHandle<()>,
}
impl Drop for Local {
    fn drop(&mut self) {
        self.join.abort();
    }
}
async fn local() -> Local {
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let requests = Arc::new(Mutex::new(vec![]));
    let record = requests.clone();
    let (release, gate) = watch::channel(false);
    let join = tokio::spawn(async move {
        let mut clients = tokio::task::JoinSet::new();
        loop {
            tokio::select! {result=listener.accept()=>{let (mut s,_)=result.unwrap();let record=record.clone();let mut gate=gate.clone();clients.spawn(async move{let mut bytes=vec![];loop{let mut b=[0;1024];let n=s.read(&mut b).await.unwrap();if n==0{return;}bytes.extend_from_slice(&b[..n]);if bytes.ends_with(b"\r\n\r\n"){break;}}let request=String::from_utf8(bytes).unwrap();let ranged=request.to_lowercase().contains("range: bytes=3-");record.lock().unwrap().push(request);let header=if ranged{"HTTP/1.1 206 Partial Content\r\nContent-Length: 3\r\nContent-Range: bytes 3-5/6\r\nETag: \"probe\"\r\nConnection: close\r\n\r\nd"}else{"HTTP/1.1 200 OK\r\nContent-Length: 6\r\nETag: \"probe\"\r\nConnection: close\r\n\r\nabc"};if s.write_all(header.as_bytes()).await.is_err(){return;}while !*gate.borrow(){if gate.changed().await.is_err(){return;}}let _=s.write_all(if ranged {b"ef"}else{b"def"}).await;});},_=clients.join_next(),if !clients.is_empty()=>{}}
        }
    });
    Local {
        origin,
        release,
        join,
    }
}
fn temp() -> tempfile::TempDir {
    tempfile::tempdir_in(std::env::temp_dir().canonicalize().unwrap()).unwrap()
}

#[tokio::test]
async fn controls_and_shutdown_reset_progress_without_observing_paused_state() {
    use furchive_app::{browse::assemble_pool_page, downloads::Downloads, settings::Settings};
    use furchive_domain::{Pool, PoolKey, RatingFilter};
    for limit in [1, 2, 4] {
        for mode in ["single", "group", "shutdown"] {
            let t = temp();
            let root = t.path().join("media");
            std::fs::create_dir(&root).unwrap();
            let server = local().await;
            let db = t.path().join("q");
            let hub = Downloads::fixture(db.clone(), server.origin.clone()).unwrap();
            hub.select_root_with_concurrency(&root, limit)
                .await
                .unwrap();
            let mut p = post("1");
            p.rating = furchive_domain::Rating::Safe;
            let mut pool = Pool::new(PoolKey::new("e621", "3").unwrap(), "Owned".into());
            pool.post_ids = vec!["1".into()];
            let page = assemble_pool_page(pool, vec![p], 0, 1, &RatingFilter::Any).unwrap();
            let group = hub
                .enqueue_pool_page(&page, &Settings::default())
                .await
                .unwrap();
            hub.control(1, "resume").await.unwrap();
            tokio::time::timeout(Duration::from_secs(3), async {
                while hub.jobs().await.unwrap()[0].bytes != 3 {
                    tokio::time::sleep(Duration::from_millis(5)).await;
                }
            })
            .await
            .unwrap();
            match mode {
                "single" => hub.control(1, "pause").await.unwrap(),
                "group" => hub.control_group(&group, "pause").await.unwrap(),
                _ => {
                    hub.shutdown().await.unwrap();
                    hub.select_root_with_concurrency(&root, limit)
                        .await
                        .unwrap();
                }
            }
            hub.control(1, "resume").await.unwrap();
            // Independent DB observation does not accidentally reset the service sampler.
            tokio::time::timeout(Duration::from_secs(3), async {
                loop {
                    let conn = rusqlite::Connection::open(&db).unwrap();
                    let text: String = conn
                        .query_row("SELECT payload FROM jobs WHERE id=1", [], |r| r.get(0))
                        .unwrap();
                    let job: serde_json::Value = serde_json::from_str(&text).unwrap();
                    if job["bytes"] == 4 {
                        break;
                    }
                    tokio::time::sleep(Duration::from_millis(5)).await;
                }
            })
            .await
            .unwrap();
            let rows = hub.jobs().await.unwrap();
            assert!(
                rows[0].bytes_per_second.is_none(),
                "first resumed sample retained history mode={mode} limit={limit}"
            );
            assert!(rows[0].eta_seconds.is_none());
            hub.shutdown().await.unwrap();
            let _ = server.release.send(true);
            drop(server);
        }
    }
}
