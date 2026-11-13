//! Whole-pool acquisition uses one authoritative snapshot, bounded batches, and safe ratings.
use furchive_app::browse::{Provider, collect_pool_download};
use furchive_domain::{MediaItem, Pool, PoolKey, PostKey, Rating, RatingFilter, SearchQuery};
use std::sync::{Arc, Mutex};
#[derive(Clone)]
struct Fixture {
    calls: Arc<Mutex<Vec<Vec<String>>>>,
    fail: bool,
    huge: bool,
}
impl Provider for Fixture {
    async fn search(
        &self,
        _: &SearchQuery,
        _: u32,
        _: usize,
    ) -> Result<furchive_e621::SearchPage, String> {
        Err("unused".into())
    }
    async fn pool(&self, _: u64) -> Result<Pool, String> {
        let mut p = Pool::new(PoolKey::new("e621", "3").unwrap(), "Owned pool".into());
        p.post_ids = (1..=if self.huge { 10001 } else { 205 })
            .map(|id| id.to_string())
            .collect();
        Ok(p)
    }
    async fn posts(&self, ids: &[String]) -> Result<Vec<MediaItem>, String> {
        self.calls.lock().unwrap().push(ids.to_vec());
        if self.fail && ids[0] == "101" {
            return Err("owned interrupted metadata".into());
        }
        Ok(ids
            .iter()
            .rev()
            .filter(|id| id.as_str() != "2")
            .map(|id| {
                let mut p = MediaItem::new(PostKey::new("e621", id).unwrap());
                p.rating = if id == "101" {
                    Rating::Questionable
                } else {
                    Rating::Safe
                };
                p
            })
            .collect())
    }
    async fn suggestions(&self, _: &str) -> Result<Vec<furchive_e621::TagSuggestion>, String> {
        Ok(vec![])
    }
}
#[tokio::test]
async fn whole_pool_batches_exact_snapshot_and_keeps_hidden_rating_holes_and_ordinals() {
    let f = Fixture {
        calls: Default::default(),
        fail: false,
        huge: false,
    };
    let page = collect_pool_download(&f, 3, &RatingFilter::default())
        .await
        .unwrap();
    assert_eq!(
        f.calls
            .lock()
            .unwrap()
            .iter()
            .map(Vec::len)
            .collect::<Vec<_>>(),
        [100, 100, 5]
    );
    assert_eq!(page.entries.len(), 205);
    assert_eq!(page.entries[100].position, 100);
    assert!(page.entries[1].post.is_none());
    assert!(page.entries[100].post.is_none());
    assert_eq!(page.entries[204].post.as_ref().unwrap().key.id(), "205");
    assert_eq!(page.next_offset, None);
}
#[tokio::test]
async fn whole_pool_rejects_oversize_before_batch_requests_and_does_not_return_partial_on_failure()
{
    for (huge, expected) in [(true, 0), (false, 2)] {
        let f = Fixture {
            calls: Default::default(),
            fail: true,
            huge,
        };
        assert!(
            collect_pool_download(&f, 3, &RatingFilter::default())
                .await
                .is_err()
        );
        assert_eq!(f.calls.lock().unwrap().len(), expected);
    }
}
