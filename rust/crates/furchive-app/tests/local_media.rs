#![cfg(all(feature = "fixture-downloads", feature = "fixture-media"))]
use furchive_app::{
    downloads::Downloads,
    media_cache::{MediaCache, Variant},
    settings::Settings,
};
use furchive_domain::{MediaItem, PostKey, Rating};
use std::io::{Read, Write};
fn post() -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", "100").unwrap());
    p.rating = Rating::Safe;
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.file_extension = Some("png".into());
    p
}
#[tokio::test]
async fn local_completed_media_uses_grant_and_receipt_after_restart_without_network() {
    let base = std::env::temp_dir().canonicalize().unwrap();
    let t = tempfile::tempdir_in(base).unwrap();
    let art = t.path().join("art");
    std::fs::create_dir(&art).unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let bytes = include_bytes!("../../../apps/desktop/public/fixtures/still.png");
    let server = std::thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let mut b = [0; 4096];
        assert!(socket.read(&mut b).unwrap() > 0);
        write!(
            socket,
            "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
            bytes.len()
        )
        .unwrap();
        socket.write_all(bytes).unwrap();
    });
    let db = t.path().join("q.sqlite");
    let hub = Downloads::fixture(db.clone(), origin.clone()).unwrap();
    hub.select_root(&art).await.unwrap();
    let p = post();
    let settings = Settings {
        filename_template: "{source}/{id}.{ext}".into(),
        ..Default::default()
    };
    let id = hub.enqueue(&p, &settings).await.unwrap();
    hub.control(id, "resume").await.unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(3), async {
        while hub.jobs().await.unwrap()[0].state != "Completed" {
            tokio::time::sleep(std::time::Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    hub.shutdown().await.unwrap();
    server.join().unwrap();
    // Fixture server is gone. Exact current-page grants remain mandatory even for owned art.
    let hub = Downloads::new(db);
    let cache = MediaCache::local_fixture(1024 * 1024, &origin).unwrap();
    cache.authorize(std::slice::from_ref(&p)).unwrap();
    assert!(
        cache
            .prepare_with_downloads("100", Variant::Full, &hub)
            .await
            .is_err()
    );
    hub.select_root(&art).await.unwrap();
    assert!(
        cache
            .prepare_with_downloads("missing", Variant::Full, &hub)
            .await
            .is_err()
    );
    let handle = cache
        .prepare_with_downloads("100", Variant::Full, &hub)
        .await
        .unwrap();
    assert_eq!(cache.get(&handle).unwrap().bytes.as_slice(), bytes);
    assert_eq!(cache.get(&handle).unwrap().mime, "image/png");
    let mut changed = p.clone();
    changed.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb1111111111111111111111111111.png".into());
    cache.authorize(&[changed]).unwrap();
    assert!(
        cache
            .prepare_with_downloads("100", Variant::Full, &hub)
            .await
            .is_err(),
        "new upstream version cannot read old post bytes as current"
    );
    cache.authorize(std::slice::from_ref(&p)).unwrap();
    std::fs::write(art.join("e621/100.png"), b"tampered").unwrap();
    assert!(
        cache
            .prepare_with_downloads("100", Variant::Full, &hub)
            .await
            .is_err()
    );
    std::fs::write(art.join("e621/100.png"), bytes).unwrap();
    cache.authorize(&[p]).unwrap();
    let h = cache
        .prepare_with_downloads("100", Variant::Full, &hub)
        .await
        .unwrap();
    cache.authorize(&[]).unwrap();
    assert!(cache.get(&h).is_none());
    assert!(
        cache
            .prepare_with_downloads("100", Variant::Full, &hub)
            .await
            .is_err()
    );
    hub.shutdown().await.unwrap();
}
