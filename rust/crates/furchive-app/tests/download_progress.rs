use furchive_app::download_progress::{Observation, ProgressSampler};
use std::time::{Duration, Instant};
#[test]
fn progress_rate_eta_are_observed_not_invented_and_reset_on_pause_or_counter_regression() {
    let start = Instant::now();
    let mut sampler = ProgressSampler::default();
    let first = sampler.sample(
        1,
        Observation {
            bytes: 100,
            total: Some(1100),
            running: true,
        },
        start,
    );
    assert!(first.bytes_per_second.is_none());
    assert!(first.eta_seconds.is_none());
    let second = sampler.sample(
        1,
        Observation {
            bytes: 600,
            total: Some(1100),
            running: true,
        },
        start + Duration::from_millis(500),
    );
    assert_eq!(second.bytes_per_second, Some(1000.0));
    assert_eq!(second.eta_seconds, Some(0.5));
    let paused = sampler.sample(
        1,
        Observation {
            bytes: 600,
            total: Some(1100),
            running: false,
        },
        start + Duration::from_secs(1),
    );
    assert!(paused.bytes_per_second.is_none());
    let resumed = sampler.sample(
        1,
        Observation {
            bytes: 600,
            total: Some(1100),
            running: true,
        },
        start + Duration::from_secs(2),
    );
    assert!(resumed.bytes_per_second.is_none());
    let regressed = sampler.sample(
        1,
        Observation {
            bytes: 1,
            total: Some(1100),
            running: true,
        },
        start + Duration::from_secs(3),
    );
    assert!(regressed.bytes_per_second.is_none());
    let unknown = sampler.sample(
        2,
        Observation {
            bytes: 10,
            total: None,
            running: true,
        },
        start,
    );
    assert!(unknown.eta_seconds.is_none());
}
#[test]
fn sampler_forgets_absent_jobs_instead_of_accumulating_history_forever() {
    let mut s = ProgressSampler::default();
    let now = Instant::now();
    s.sample(
        1,
        Observation {
            bytes: 1,
            total: Some(2),
            running: true,
        },
        now,
    );
    s.retain(&[]);
    let result = s.sample(
        1,
        Observation {
            bytes: 2,
            total: Some(2),
            running: true,
        },
        now + Duration::from_secs(1),
    );
    assert!(result.bytes_per_second.is_none());
}
