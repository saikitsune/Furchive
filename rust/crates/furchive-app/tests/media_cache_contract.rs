//! Owned harmless cache/policy fixtures; not live artwork or native decoder proof.
#![cfg(feature = "cache-test-contract")]
use furchive_app::media_cache::{MediaCache, Variant, canonical_media};
use furchive_domain::{MediaItem, PostKey};
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.preview_url = p.full_url.clone();
    p
}
#[test]
fn only_canonical_media_paths_have_fetch_authority() {
    assert!(
        canonical_media("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png")
            .is_ok()
    );
    assert!(
        canonical_media(
            "https://static1.e621.net/data/preview/aa/bb/aabb0000000000000000000000000000.jpg"
        )
        .is_ok()
    );
    for s in [
        "http://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png",
        "https://evil.test/art.png",
        "https://user@static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png",
        "https://static1.e621.net:443/data/aa/bb/aabb0000000000000000000000000000.png",
        "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.svg",
        "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png?q=1",
        "https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png#x",
        "https://static1.e621.net/data/aa/bb/%2e%2e/art.png",
        "https://static1.e621.net/data/xx/bb/aabb0000000000000000000000000000.png",
    ] {
        assert!(canonical_media(s).is_err(), "{s}");
    }
}
#[tokio::test]
async fn cache_never_fetches_ungranted_keys_and_revocation_invalidates_opaque_handles() {
    let c = MediaCache::new(1024).unwrap();
    assert!(c.prepare("missing", Variant::Full).await.is_err());
    c.authorize(&[post("1")]).unwrap();
    let grant = c.grant_for_test("1", Variant::Full).unwrap();
    let handle = c
        .publish_for_test(grant, b"owned bytes".to_vec(), "image/png")
        .unwrap();
    assert_eq!(c.get(&handle).unwrap().bytes.as_slice(), b"owned bytes");
    c.authorize(&[post("2")]).unwrap();
    assert!(c.get(&handle).is_none());
    assert!(c.grant_for_test("1", Variant::Full).is_err());
}
#[cfg(feature = "fixture-media")]
#[tokio::test]
async fn real_http_fetch_is_bounded_validated_cached_and_never_follows_redirects() {
    use std::io::{Read, Write};
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let server = std::thread::spawn(move || {
        for mode in 0..3 {
            let (mut socket, _) = listener.accept().unwrap();
            socket
                .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                .unwrap();
            let mut bytes = [0; 4096];
            let n = socket.read(&mut bytes).unwrap();
            let text = String::from_utf8_lossy(&bytes[..n]);
            assert!(text.starts_with("GET /data/aa/bb/aabb0000000000000000000000000000.png"));
            assert!(!text.to_ascii_lowercase().contains("authorization:"));
            if mode == 0 {
                let body = include_bytes!("../../../apps/desktop/public/fixtures/still.png");
                write!(
                    socket,
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                    body.len()
                )
                .unwrap();
                socket.write_all(body).unwrap();
            } else if mode == 1 {
                socket.write_all(b"HTTP/1.1 302 Found\r\nLocation: https://evil.invalid/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
            } else {
                socket
                    .write_all(
                        b"HTTP/1.1 200 OK\r\nContent-Length: 99999999\r\nConnection: close\r\n\r\n",
                    )
                    .unwrap();
            }
        }
    });
    let cache = MediaCache::local_fixture(1024 * 1024, &origin).unwrap();
    cache.authorize(&[post("1")]).unwrap();
    let h = cache.prepare("1", Variant::Full).await.unwrap();
    assert_eq!(cache.get(&h).unwrap().mime, "image/png");
    assert_eq!(cache.prepare("1", Variant::Full).await.unwrap(), h);
    cache.authorize(&[post("2")]).unwrap();
    assert!(
        cache
            .prepare("2", Variant::Full)
            .await
            .unwrap_err()
            .contains("302")
    );
    cache.authorize(&[post("3")]).unwrap();
    assert!(
        cache
            .prepare("3", Variant::Full)
            .await
            .unwrap_err()
            .contains("budget")
    );
    server.join().unwrap();
}

#[test]
fn out_of_order_query_completion_cannot_restore_old_media_authority() {
    let c = MediaCache::new(1024).unwrap();
    let first = c.begin_page().unwrap();
    let second = c.begin_page().unwrap();
    assert!(c.authorize_page(first, &[post("1")]).is_err());
    c.authorize_page(second, &[post("2")]).unwrap();
    assert!(c.grant_for_test("1", Variant::Full).is_err());
    assert!(c.grant_for_test("2", Variant::Full).is_ok());
}

#[test]
fn cache_enforces_budget_and_inflight_generation_before_publication() {
    let c = MediaCache::new(12).unwrap();
    c.authorize(&[post("1")]).unwrap();
    let old = c.grant_for_test("1", Variant::Full).unwrap();
    c.authorize(&[post("2")]).unwrap();
    assert!(c.publish_for_test(old, vec![1; 4], "image/png").is_err());
    let fresh = c.grant_for_test("2", Variant::Full).unwrap();
    let h = c
        .publish_for_test(fresh.clone(), vec![1; 8], "image/png")
        .unwrap();
    assert!(c.publish_for_test(fresh, vec![2; 13], "image/png").is_err());
    assert!(c.get(&h).is_some());
    let second = c.grant_for_test("2", Variant::Preview).unwrap();
    let newer = c.publish_for_test(second, vec![2; 8], "image/png").unwrap();
    assert!(c.get(&h).is_none());
    assert_eq!(c.get(&newer).unwrap().bytes.len(), 8);
    assert!(c.resident_bytes().unwrap() <= 12);
}
