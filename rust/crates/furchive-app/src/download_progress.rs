//! Nonpersistent observed rates; no speed/ETA is inferred before two valid samples.
use std::{
    collections::{HashMap, HashSet},
    time::Instant,
};
pub struct Observation {
    pub bytes: u64,
    pub total: Option<u64>,
    pub running: bool,
}
pub struct Progress {
    pub bytes_per_second: Option<f64>,
    pub eta_seconds: Option<f64>,
}
#[derive(Default)]
pub struct ProgressSampler {
    last: HashMap<i64, (u64, Instant)>,
}
impl ProgressSampler {
    pub fn sample(&mut self, id: i64, observation: Observation, now: Instant) -> Progress {
        let mut result = Progress {
            bytes_per_second: None,
            eta_seconds: None,
        };
        if !observation.running {
            self.last.remove(&id);
            return result;
        }
        let previous = self.last.insert(id, (observation.bytes, now));
        if let Some((bytes, time)) = previous
            && observation.bytes >= bytes
            && let Some(elapsed) = now.checked_duration_since(time)
        {
            let seconds = elapsed.as_secs_f64();
            let delta = observation.bytes - bytes;
            if seconds > 0.0 && delta > 0 {
                let rate = delta as f64 / seconds;
                if rate.is_finite() {
                    result.bytes_per_second = Some(rate);
                    result.eta_seconds = observation
                        .total
                        .filter(|total| *total >= observation.bytes)
                        .map(|total| (total - observation.bytes) as f64 / rate);
                }
            }
        }
        result
    }
    pub fn forget(&mut self, id: i64) {
        self.last.remove(&id);
    }
    pub fn clear(&mut self) {
        self.last.clear();
    }
    pub fn retain(&mut self, ids: &[i64]) {
        let ids = ids.iter().copied().collect::<HashSet<_>>();
        self.last.retain(|id, _| ids.contains(id));
    }
}
