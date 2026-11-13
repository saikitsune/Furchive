use furchive_app::{
    browse::{PoolEntry, PoolPage, assemble_pool_page},
    downloads::Downloads,
    settings::Settings,
};
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, Rating, RatingFilter};
fn post(id: &str) -> MediaItem {
    let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
    p.rating = Rating::Safe;
    p.full_url =
        Some("https://static1.e621.net/data/aa/bb/aabb0000000000000000000000000000.png".into());
    p.file_extension = Some("png".into());
    p
}
fn whole() -> PoolPage {
    let mut pool = Pool::new(PoolKey::new("e621", "3").unwrap(), "Owned pool".into());
    pool.post_ids = (1..=205).map(|id| id.to_string()).collect();
    let entries = pool
        .post_ids
        .iter()
        .enumerate()
        .map(|(position, id)| PoolEntry {
            id: id.clone(),
            position,
            post: if id == "2" || id == "101" {
                None
            } else {
                Some(post(id))
            },
        })
        .collect();
    PoolPage {
        pool,
        entries,
        total: 205,
        next_offset: None,
    }
}
fn temp() -> tempfile::TempDir {
    let base = std::env::temp_dir().canonicalize().unwrap();
    tempfile::Builder::new()
        .prefix("furchive-whole-")
        .tempdir_in(base)
        .unwrap()
}
#[tokio::test]
async fn whole_pool_queue_is_one_paused_group_with_authoritative_holes_and_reopens() {
    let t = temp();
    let root = t.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(t.path().join("q.sqlite"));
    hub.select_root(&root).await.unwrap();
    let group = hub
        .enqueue_whole_pool(&whole(), &Settings::default())
        .await
        .unwrap();
    let jobs = hub.jobs().await.unwrap();
    assert_eq!(jobs.len(), 203);
    assert!(
        jobs.iter()
            .all(|j| j.state == "Paused" && j.group_id.as_deref() == Some(&group))
    );
    assert!(jobs[1].relative_path.ends_with("3_3.png"));
    assert_eq!(jobs[99].group_position, Some(101));
    hub.shutdown().await.unwrap();
    let hub = Downloads::new(t.path().join("q.sqlite"));
    hub.select_root(&root).await.unwrap();
    assert_eq!(hub.jobs().await.unwrap().len(), 203);
    hub.shutdown().await.unwrap();
}
#[tokio::test]
async fn incomplete_or_invalid_whole_pool_never_inserts_partial_group() {
    let t = temp();
    let root = t.path().join("art");
    std::fs::create_dir(&root).unwrap();
    let hub = Downloads::new(t.path().join("q.sqlite"));
    hub.select_root(&root).await.unwrap();
    let mut p = whole();
    p.entries.pop();
    assert!(
        hub.enqueue_whole_pool(&p, &Settings::default())
            .await
            .is_err()
    );
    let mut p = whole();
    p.entries[204].post.as_mut().unwrap().full_url = Some("https://example.com/private".into());
    assert!(
        hub.enqueue_whole_pool(&p, &Settings::default())
            .await
            .is_err()
    );
    assert!(hub.jobs().await.unwrap().is_empty());
    let small = assemble_pool_page(
        whole().pool,
        vec![post("1")],
        0,
        50,
        &RatingFilter::default(),
    )
    .unwrap();
    assert!(
        hub.enqueue_whole_pool(&small, &Settings::default())
            .await
            .is_err()
    );
    hub.shutdown().await.unwrap();
}
