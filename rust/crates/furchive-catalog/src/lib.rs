//! Bounded streaming CSV/gzip pool import. Caller stages database writes and commits only on success.
use furchive_domain::{Pool, PoolKey};
use std::io::{self, Read};
#[derive(Debug, Clone, Copy)]
pub struct Limits {
    pub compressed: u64,
    pub decompressed: u64,
    pub rows: usize,
}
impl Default for Limits {
    fn default() -> Self {
        Self {
            compressed: 128 * 1024 * 1024,
            decompressed: 512 * 1024 * 1024,
            rows: 1_000_000,
        }
    }
}
struct Limited<R> {
    inner: R,
    left: u64,
}
impl<R: Read> Read for Limited<R> {
    fn read(&mut self, out: &mut [u8]) -> io::Result<usize> {
        if out.is_empty() {
            return Ok(0);
        }
        if self.left == 0 {
            let mut extra = [0];
            return match self.inner.read(&mut extra)? {
                0 => Ok(0),
                _ => Err(io::Error::other("export byte limit exceeded")),
            };
        }
        let size = out.len().min(self.left as usize);
        let n = self.inner.read(&mut out[..size])?;
        self.left -= n as u64;
        Ok(n)
    }
}
pub fn read_pools_gzip(
    source: impl Read,
    limits: Limits,
    mut accept: impl FnMut(Pool) -> Result<(), String>,
) -> Result<usize, String> {
    let compressed = Limited {
        inner: source,
        left: limits.compressed,
    };
    let gzip = flate2::read::MultiGzDecoder::new(compressed);
    let decoded = Limited {
        inner: gzip,
        left: limits.decompressed,
    };
    let mut csv = csv::ReaderBuilder::new()
        .has_headers(true)
        .from_reader(decoded);
    let headers = csv.headers().map_err(|_| "invalid export header")?.clone();
    let unique_headers: std::collections::HashSet<_> = headers.iter().collect();
    if unique_headers.len() != headers.len() || headers.iter().any(|h| h.trim().is_empty()) {
        return Err("ambiguous export header".into());
    }
    let index = |name: &str| headers.iter().position(|h| h == name);
    let id = index("id").ok_or("export missing id")?;
    let name = index("name").ok_or("export missing name")?;
    let posts = index("post_ids").ok_or("export missing post_ids")?;
    let description = index("description");
    let active = index("is_active");
    let category = index("category");
    let mut count = 0;
    let mut seen = std::collections::HashSet::new();
    for row in csv.records() {
        let row = row.map_err(|_| "invalid or oversized pool export")?;
        if count >= limits.rows {
            return Err("export row limit exceeded".into());
        }
        let pid = row
            .get(id)
            .ok_or("missing pool ID")?
            .parse::<u64>()
            .map_err(|_| "invalid pool ID")?;
        if pid == 0 || !seen.insert(pid) {
            return Err("invalid or duplicate pool ID".into());
        }
        let pool_name = row.get(name).ok_or("missing pool name")?;
        if pool_name.trim().is_empty() {
            return Err("empty pool name".into());
        }
        let mut pool = Pool::new(
            PoolKey::new("e621", pid.to_string()).map_err(|e| e.to_string())?,
            pool_name.into(),
        );
        pool.description = description
            .and_then(|i| row.get(i))
            .filter(|s| *s != "NULL")
            .map(str::to_owned);
        pool.category = category
            .and_then(|i| row.get(i))
            .filter(|s| *s != "NULL")
            .map(str::to_owned);
        pool.active = !matches!(active.and_then(|i| row.get(i)), Some("f" | "false" | "0"));
        let raw = row.get(posts).ok_or("missing pool order")?;
        if raw != "NULL" {
            for value in raw
                .trim_matches(['{', '}', '[', ']'])
                .split(|c: char| c.is_ascii_whitespace() || c == ',')
                .filter(|s| !s.is_empty())
            {
                let id = value.parse::<u64>().map_err(|_| "invalid export post ID")?;
                if id == 0 {
                    return Err("invalid export post ID".into());
                }
                pool.post_ids.push(id.to_string());
            }
        }
        let unique: std::collections::HashSet<_> = pool.post_ids.iter().collect();
        if unique.len() != pool.post_ids.len() {
            return Err("duplicate pool member in export".into());
        }
        accept(pool)?;
        count += 1;
    }
    Ok(count)
}
