//! Old databases are never handed to SQLite. Only stable bounded private copies
//! of main + WAL are opened READ_ONLY; SHM is intentionally never copied/opened.
use super::{
    MAX_DATABASE_BYTES, MAX_RECORDS, MAX_TEXT_BYTES, PinPreview, QueuePreview, SessionPreview,
    digest, json, read_optional, valid_text,
};
use furchive_domain::PoolKey;
use rusqlite::{Connection, OpenFlags, limits::Limit};
use std::path::Path;

fn sql<T>(result: rusqlite::Result<T>) -> Result<T, String> {
    result.map_err(|_| "legacy SQLite invalid, unsupported, or resource limit exceeded".into())
}

struct Snapshot {
    conn: Connection,
    // Drop connection before removing scratch WAL/SHM on Windows.
    _directory: tempfile::TempDir,
}

fn snapshot(root: &Path, relative: &str) -> Result<Option<Snapshot>, String> {
    let path = root.join(relative);
    let wal_path = path.with_file_name(format!(
        "{}-wal",
        path.file_name()
            .ok_or("legacy path invalid")?
            .to_string_lossy()
    ));
    let journal_path = path.with_file_name(format!(
        "{}-journal",
        path.file_name()
            .ok_or("legacy path invalid")?
            .to_string_lossy()
    ));
    let main = read_optional(&path, MAX_DATABASE_BYTES)?;
    let wal = read_optional(&wal_path, MAX_DATABASE_BYTES)?;
    let journal = read_optional(&journal_path, MAX_DATABASE_BYTES)?;
    if journal.as_ref().is_some_and(|b| !b.is_empty()) {
        return Err("legacy rollback journal requires offline manual recovery".into());
    }
    let Some(main) = main else {
        if wal.is_some() || journal.is_some() {
            return Err("orphan legacy database sidecar".into());
        }
        return Ok(None);
    };
    // Refuse a moving source. Callers must stop C# first; this is not a hot-backup
    // protocol against hostile same-user writers that change-and-restore bytes.
    if read_optional(&path, MAX_DATABASE_BYTES)?
        .as_ref()
        .map(|b| digest(b))
        != Some(digest(&main))
        || read_optional(&wal_path, MAX_DATABASE_BYTES)?
            .as_ref()
            .map(|b| digest(b))
            != wal.as_ref().map(|b| digest(b))
        || read_optional(&journal_path, MAX_DATABASE_BYTES)? != journal
    {
        return Err("legacy database changed during snapshot; stop the old app".into());
    }
    if let Some(wal) = &wal {
        super::wal::validate(&main, wal)?;
    }
    let directory = tempfile::tempdir().map_err(|_| "cannot create private legacy snapshot")?;
    let private = directory.path().join("source.sqlite");
    std::fs::write(&private, main).map_err(|_| "cannot stage legacy snapshot")?;
    if let Some(wal) = wal {
        std::fs::write(directory.path().join("source.sqlite-wal"), wal)
            .map_err(|_| "cannot stage legacy WAL snapshot")?;
    }
    let conn = sql(Connection::open_with_flags(
        &private,
        OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
    ))?;
    for (limit, value) in [
        (Limit::SQLITE_LIMIT_LENGTH, 2 * 1024 * 1024),
        (Limit::SQLITE_LIMIT_SQL_LENGTH, 8192),
        (Limit::SQLITE_LIMIT_COLUMN, 64),
        (Limit::SQLITE_LIMIT_EXPR_DEPTH, 32),
        (Limit::SQLITE_LIMIT_COMPOUND_SELECT, 0),
        (Limit::SQLITE_LIMIT_VDBE_OP, 10000),
        (Limit::SQLITE_LIMIT_ATTACHED, 0),
        (Limit::SQLITE_LIMIT_VARIABLE_NUMBER, 32),
        (Limit::SQLITE_LIMIT_TRIGGER_DEPTH, 0),
        (Limit::SQLITE_LIMIT_WORKER_THREADS, 0),
    ] {
        sql(conn.set_limit(limit, value))?;
    }
    let mut ticks = 0usize;
    conn.progress_handler(
        1000,
        Some(move || {
            ticks += 1;
            ticks > 1000
        }),
    );
    sql(conn.pragma_update(None, "trusted_schema", false))?;
    sql(conn.pragma_update(None, "query_only", true))?;
    sql(conn.busy_timeout(std::time::Duration::ZERO))?;
    Ok(Some(Snapshot {
        conn,
        _directory: directory,
    }))
}

fn require_table(conn: &Connection, name: &str) -> Result<(), String> {
    let ordinary: bool = sql(conn.query_row("SELECT count(*)=1 FROM sqlite_schema WHERE name=?1 AND type='table' AND sql NOT LIKE 'CREATE VIRTUAL TABLE%'", [name], |r| r.get(0)))?;
    if !ordinary {
        return Err("legacy required ordinary table missing".into());
    }
    // No generated/hidden columns: SELECT must never execute schema expressions.
    // Fixed PRAGMA strings only, no SQL assembled from input.
    let pragma = match name {
        "pinned_pools" => "PRAGMA table_xinfo(pinned_pools)",
        "meta" => "PRAGMA table_xinfo(meta)",
        "downloads" => "PRAGMA table_xinfo(downloads)",
        _ => return Err("unsupported legacy table".into()),
    };
    let mut stmt = sql(conn.prepare(pragma))?;
    let mut rows = sql(stmt.query([]))?;
    let mut count = 0;
    while let Some(row) = sql(rows.next())? {
        count += 1;
        let hidden: i64 = sql(row.get(6))?;
        if hidden != 0 || count > 64 {
            return Err("legacy generated or hidden columns unsupported".into());
        }
    }
    Ok(())
}

pub(super) fn pins(root: &Path, warnings: &mut Vec<String>) -> Result<Vec<PinPreview>, String> {
    let Some(snapshot) = snapshot(root, "cache/e621/pinned_pools.sqlite")? else {
        return Ok(Vec::new());
    };
    require_table(&snapshot.conn, "pinned_pools")?;
    // CASE bounds returned values before allocating Rust strings. No old SQL,
    // triggers, user functions, views or virtual tables are executed.
    let mut stmt = sql(snapshot.conn.prepare("SELECT CASE WHEN typeof(id)='integer' AND id BETWEEN 1 AND 2147483647 THEN id END, CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB))<=4096 THEN name END, CASE WHEN typeof(post_count)='integer' AND post_count BETWEEN 0 AND 2147483647 THEN post_count END FROM pinned_pools LIMIT 1001"))?;
    let mut rows = sql(stmt.query([]))?;
    let mut pins = Vec::new();
    let mut count = 0;
    while let Some(row) = sql(rows.next())? {
        count += 1;
        if count > MAX_RECORDS {
            return Err("legacy pin record limit".into());
        }
        let id: Option<i64> = sql(row.get(0))?;
        let name: Option<String> = sql(row.get(1))?;
        let posts: Option<u32> = sql(row.get(2))?;
        if let (Some(id), Some(name), Some(post_count)) = (id, name, posts)
            && valid_text(&name)
        {
            let key =
                PoolKey::new("e621", id.to_string()).map_err(|_| "invalid legacy pool identity")?;
            if pins.iter().any(|p: &PinPreview| p.key == key) {
                return Err("duplicate legacy pin identity".into());
            }
            pins.push(PinPreview {
                key,
                name,
                post_count,
            });
            continue;
        }
        warnings.push(format!(
            "Legacy pin {count} rejected: invalid or unrepresentable fields"
        ));
    }
    Ok(pins)
}

pub(super) fn session(
    root: &Path,
    warnings: &mut Vec<String>,
) -> Result<Option<SessionPreview>, String> {
    let Some(snapshot) = snapshot(root, "cache/e621/last_session.sqlite")? else {
        return Ok(None);
    };
    require_table(&snapshot.conn, "meta")?;
    let mut stmt = sql(snapshot.conn.prepare("SELECT CASE WHEN typeof(value)='text' AND length(CAST(value AS BLOB))<=65536 THEN value END FROM meta WHERE key='last_session' LIMIT 2"))?;
    let mut rows = sql(stmt.query([]))?;
    let Some(row) = sql(rows.next())? else {
        return Ok(None);
    };
    let raw: Option<String> = sql(row.get(0))?;
    if sql(rows.next())?.is_some() {
        return Err("duplicate legacy session container".into());
    }
    let Some(raw) = raw else {
        warnings.push("Legacy session rejected: invalid value".into());
        return Ok(None);
    };
    let v = json::parse(raw.as_bytes())?;
    if !v.is_object() {
        return Err("legacy session must be an object".into());
    }
    let parsed = (|| {
        let query = json::query(&v, "Include", "Exclude")?;
        let page = match v.get("Page") {
            None => 1,
            Some(v) => u32::try_from(v.as_u64()?).ok().filter(|p| *p > 0)?,
        };
        let pool_mode = match v.get("IsPoolMode") {
            None => false,
            Some(v) => v.as_bool()?,
        };
        let pool = match v.get("PoolId") {
            None | Some(serde_json::Value::Null) => None,
            Some(v) => Some(
                PoolKey::new("e621", v.as_u64()?.to_string())
                    .ok()
                    .filter(|_| (1..=i32::MAX as u64).contains(&v.as_u64().unwrap_or(0)))?,
            ),
        };
        if pool_mode && pool.is_none() {
            return None;
        }
        Some(SessionPreview {
            query,
            page,
            pool: if pool_mode { pool } else { None },
        })
    })();
    if parsed.is_none() {
        warnings.push("Legacy session rejected: invalid or unrepresentable fields".into());
    }
    Ok(parsed)
}

pub(super) fn queue(root: &Path) -> Result<QueuePreview, String> {
    let Some(snapshot) = snapshot(root, "downloads.sqlite")? else {
        return Ok(QueuePreview::Absent);
    };
    require_table(&snapshot.conn, "downloads")?;
    // Count only; never read destination, URL, credentials, errors or media data.
    let records: usize = sql(snapshot.conn.query_row(
        "SELECT count(*) FROM (SELECT 1 FROM downloads LIMIT 1001)",
        [],
        |r| r.get(0),
    ))?;
    if records > MAX_RECORDS {
        return Err("legacy download record limit".into());
    }
    Ok(QueuePreview::Quarantined { records })
}

// Keep constants in one place; SQL literals intentionally mirror audited bounds.
const _: () = assert!(MAX_TEXT_BYTES == 4096 && MAX_RECORDS == 1000);
