#![cfg(feature = "fixture-media")]
use furchive_app::{account_session::AccountSession, downloads::Downloads, settings::Settings};
use furchive_domain::RatingFilter;
use std::{
    io::{Read, Write},
    sync::{Arc, Mutex},
};
#[tokio::test]
async fn session_whole_pool_uses_only_filtered_bounded_requests_and_commits_paused_once() {
    let base = std::env::temp_dir().canonicalize().unwrap();
    let t = tempfile::tempdir_in(base).unwrap();
    let root = t.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(t.path().join("q"));
    hub.select_root(&root).await.unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let requests = Arc::new(Mutex::new(vec![]));
    let calls = requests.clone();
    let server = std::thread::spawn(move || {
        for n in 0..3 {
            let (mut s, _) = listener.accept().unwrap();
            s.set_read_timeout(Some(std::time::Duration::from_secs(3)))
                .unwrap();
            let mut b = [0; 8192];
            let count = s.read(&mut b).unwrap();
            let req = String::from_utf8_lossy(&b[..count]).to_string();
            assert!(!req.contains("filter_id"));
            assert!(!req.to_lowercase().contains("authorization:"));
            calls.lock().unwrap().push(req);
            let body = if n == 0 {
                serde_json::json!({"id":3,"name":"Owned pool","post_ids":(1..=105).collect::<Vec<_>>()})
            } else {
                let id = if n == 1 { 2 } else { 103 };
                serde_json::json!({"posts":[{"id":id,"rating":"s","tags":{"artist":[]},"file":{"url":"https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png","ext":"png"},"score":{"total":0}}]})
            };
            let data = serde_json::to_vec(&body).unwrap();
            write!(
                s,
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                data.len()
            )
            .unwrap();
            s.write_all(&data).unwrap();
        }
    });
    let session = AccountSession::new(furchive_e621::E621Client::local_fixture(&origin).unwrap());
    let group = session
        .queue_whole_pool(3, &RatingFilter::default(), &hub, &Settings::default())
        .await
        .unwrap();
    let jobs = hub.jobs().await.unwrap();
    assert_eq!(jobs.len(), 2);
    assert_eq!(
        jobs.iter().map(|j| j.group_position).collect::<Vec<_>>(),
        [Some(1), Some(102)]
    );
    assert!(
        jobs.iter()
            .all(|j| j.state == "Paused" && j.group_id.as_deref() == Some(&group))
    );
    assert!(jobs[1].relative_path.ends_with("103_103.png"));
    server.join().unwrap();
    assert!(requests.lock().unwrap()[0].starts_with("GET /pools/3.json"));
    hub.shutdown().await.unwrap();
}
