use furchive_domain::{FilenameTemplate, MediaItem, PostKey, TemplateValues};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("I/O: {0}")]
    Io(#[from] std::io::Error),
    #[error("SQLite: {0}")]
    Sql(#[from] rusqlite::Error),
    #[error("JSON: {0}")]
    Json(#[from] serde_json::Error),
    #[error("invalid intent: {0}")]
    Invalid(String),
    #[error("engine already owned or worker already started")]
    Busy,
    #[error("worker stopped")]
    Stopped,
}
pub type Result<T> = std::result::Result<T, Error>;

pub(crate) fn validate_url(raw: &str) -> Result<url::Url> {
    let url = url::Url::parse(raw).map_err(|_| Error::Invalid("invalid URL".into()))?;
    if url.scheme() != "https"
        || !matches!(
            url.host_str(),
            Some("e621.net" | "static1.e621.net" | "static2.e621.net" | "static3.e621.net")
        )
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
        || url.fragment().is_some()
    {
        return Err(Error::Invalid(
            "URL outside canonical credential-free e621/CDN HTTPS allowlist".into(),
        ));
    }
    Ok(url)
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DuplicatePolicy {
    Skip,
    Overwrite,
    Rename,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub enum ExpectedHash {
    Sha256(String),
    Md5(String),
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Intent {
    pub(crate) key: PostKey,
    pub(crate) url: String,
    pub(crate) relative: String,
    pub(crate) policy: DuplicatePolicy,
    pub(crate) size: Option<u64>,
    pub(crate) hash: Option<ExpectedHash>,
}
impl Intent {
    pub(crate) fn validate(&self) -> Result<()> {
        validate_url(&self.url)?;
        crate::files::relative(&self.relative)?;
        if self.size.is_some_and(|n| n > i64::MAX as u64) {
            return Err(Error::Invalid("size too large".into()));
        }
        if let Some(hash) = &self.hash {
            let (text, length) = match hash {
                ExpectedHash::Sha256(s) => (s, 64),
                ExpectedHash::Md5(s) => (s, 32),
            };
            if text.len() != length || !text.bytes().all(|b| b.is_ascii_hexdigit()) {
                return Err(Error::Invalid("invalid hash".into()));
            }
        }
        Ok(())
    }
    pub fn new(
        media: &MediaItem,
        template: &FilenameTemplate,
        values: &TemplateValues<'_>,
        policy: DuplicatePolicy,
        hash: Option<ExpectedHash>,
    ) -> Result<Self> {
        let relative = template
            .render(values)
            .map_err(|e| Error::Invalid(e.to_string()))?;
        Ok(Self {
            key: media.key.clone(),
            url: media
                .full_url
                .clone()
                .ok_or_else(|| Error::Invalid("missing full_url".into()))?,
            relative,
            policy,
            size: media.file_size_bytes,
            hash,
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum State {
    Queued,
    Running,
    Paused,
    Cancelled,
    Completed,
    Skipped,
    Failed,
    Publishing,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Job {
    pub id: i64,
    pub key: PostKey,
    pub state: State,
    pub bytes: u64,
    pub relative_path: String,
    pub error: Option<String>,
    #[serde(default)]
    pub group_id: Option<String>,
    #[serde(default)]
    pub group_label: Option<String>,
    #[serde(default)]
    pub group_position: Option<u32>,
    pub(crate) intent: Intent,
    pub(crate) validator: Option<String>,
    pub(crate) total: Option<u64>,
    #[serde(default)]
    pub(crate) publication: Option<Publication>,
}

impl Job {
    pub fn expected_total(&self) -> Option<u64> {
        self.total.or(self.intent.size)
    }
}

// Read existing schema-1 Unix tuples, write complete platform-native receipts.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub(crate) enum Publication {
    Legacy((u64, u64, String)),
    Native {
        identity: crate::files::FileIdentity,
        digest: String,
    },
}
impl Publication {
    pub(crate) fn parts(self) -> (crate::files::FileIdentity, String) {
        match self {
            Self::Legacy((device, inode, digest)) => {
                (crate::files::FileIdentity::Unix { device, inode }, digest)
            }
            Self::Native { identity, digest } => (identity, digest),
        }
    }
}

pub struct Engine {
    pub(crate) db: std::sync::Mutex<rusqlite::Connection>,
    pub(crate) grant: crate::files::Grant,
    reserved_paths: Vec<String>,
    pub(crate) started: std::sync::atomic::AtomicBool,
    pub(crate) wake: tokio::sync::Notify,
    #[cfg(any(test, feature = "fixture-http"))]
    pub(crate) loopback: Option<String>,
    #[cfg(test)]
    pub(crate) crash_at: std::sync::atomic::AtomicU8,
}
fn valid_group(id: &str, label: &str) -> bool {
    !id.is_empty()
        && id.len() <= 128
        && id
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'-' | b'_'))
        && !label.is_empty()
        && label.len() <= 256
        && !label.chars().any(char::is_control)
}
impl Engine {
    pub fn open(db: impl AsRef<Path>, root: impl AsRef<Path>) -> Result<Self> {
        let grant = crate::files::Grant::open(root.as_ref())?;
        // Native case/normalization and NTFS 8.3 aliases cannot be fenced by a
        // portable lexical name check. Keep trusted application DB outside the
        // media grant on these targets; compare pinned ancestor identities.
        #[cfg(any(windows, target_os = "macos"))]
        crate::files::database_outside_grant(db.as_ref(), &grant)?;
        let connection = rusqlite::Connection::open(db.as_ref())?;
        let root_path = std::fs::canonicalize(root.as_ref())?;
        let db_path = std::fs::canonicalize(db.as_ref())?;
        let reserved_paths = db_path
            .strip_prefix(&root_path)
            .ok()
            .map(|p| {
                let path = p.to_string_lossy();
                ["", "-wal", "-shm", "-journal"]
                    .iter()
                    .map(|suffix| format!("{path}{suffix}"))
                    .collect()
            })
            .unwrap_or_default();
        connection.busy_timeout(std::time::Duration::from_secs(5))?;
        {
            let tx = connection.unchecked_transaction()?;
            let app: i64 = tx.pragma_query_value(None, "application_id", |r| r.get(0))?;
            let version: i64 = tx.pragma_query_value(None, "user_version", |r| r.get(0))?;
            let tables: i64 = tx.query_row(
                "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%'",
                [],
                |r| r.get(0),
            )?;
            if (app != 0 && app != 0x4655444c)
                || version > 1
                || (app == 0 && (tables > 0 || version != 0))
                || (app == 0x4655444c && version != 1)
            {
                return Err(Error::Invalid(
                    "foreign or unsupported job database schema".into(),
                ));
            }
            if version == 0 {
                tx.execute_batch("CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL); CREATE TABLE jobs(id INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT NOT NULL); PRAGMA application_id=0x4655444c; PRAGMA user_version=1;")?;
            }
            tx.execute(
                "INSERT OR IGNORE INTO settings VALUES ('root', ?)",
                [&grant.identity],
            )?;
            let saved: String =
                tx.query_row("SELECT value FROM settings WHERE key='root'", [], |r| {
                    r.get(0)
                })?;
            if saved != grant.identity {
                return Err(Error::Invalid("DB belongs to another granted root".into()));
            }
            tx.commit()?;
        }
        connection.execute_batch("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;")?;
        connection.execute(
            "INSERT OR IGNORE INTO settings VALUES ('database', lower(hex(randomblob(32))))",
            [],
        )?;
        let database: String =
            connection.query_row("SELECT value FROM settings WHERE key='database'", [], |r| {
                r.get(0)
            })?;
        grant.bind_database(&database)?;
        let engine = Self {
            db: std::sync::Mutex::new(connection),
            grant,
            reserved_paths,
            started: std::sync::atomic::AtomicBool::new(false),
            wake: tokio::sync::Notify::new(),
            #[cfg(any(test, feature = "fixture-http"))]
            loopback: None,
            #[cfg(test)]
            crash_at: std::sync::atomic::AtomicU8::new(0),
        };
        for mut job in engine.jobs()? {
            if job.state == State::Publishing {
                engine.reconcile_publication(&mut job)?;
            }
            if job.state == State::Running {
                job.state = State::Queued;
                engine.save(&job)?;
            }
            if matches!(job.state, State::Cancelled | State::Skipped) {
                engine.grant.remove_part(job.id)?;
            }
        }
        Ok(engine)
    }
    #[cfg(feature = "fixture-http")]
    pub fn fixture_http(
        db: impl AsRef<Path>,
        root: impl AsRef<Path>,
        origin: &str,
    ) -> Result<Self> {
        let url =
            url::Url::parse(origin).map_err(|_| Error::Invalid("invalid fixture origin".into()))?;
        if url.scheme() != "http"
            || url.host_str() != Some("127.0.0.1")
            || url.port().is_none()
            || !url.username().is_empty()
            || url.password().is_some()
            || url.path() != "/"
            || url.query().is_some()
            || url.fragment().is_some()
        {
            return Err(Error::Invalid(
                "fixture origin must be exact IPv4 loopback".into(),
            ));
        }
        let mut engine = Self::open(db, root)?;
        engine.loopback = Some(origin.trim_end_matches('/').into());
        Ok(engine)
    }
    #[cfg(test)]
    pub(crate) fn open_test(
        db: impl AsRef<Path>,
        root: impl AsRef<Path>,
        origin: &str,
    ) -> Result<Self> {
        let url = url::Url::parse(origin).unwrap();
        assert_eq!(url.host_str(), Some("127.0.0.1"));
        let mut engine = Self::open(db, root)?;
        engine.loopback = Some(origin.to_owned());
        Ok(engine)
    }
    pub(crate) fn save(&self, job: &Job) -> Result<()> {
        job.intent.validate()?;
        crate::files::relative(&job.relative_path)?;
        self.check_destination(&job.relative_path)?;
        self.db.lock().unwrap().execute(
            "UPDATE jobs SET payload=? WHERE id=?",
            rusqlite::params![serde_json::to_string(job)?, job.id],
        )?;
        Ok(())
    }
    /// Restore a queue without automatic network effects after the next host grant.
    pub fn hold_restored(&self) -> Result<()> {
        for mut job in self.jobs()? {
            if matches!(job.state, State::Queued | State::Running) {
                job.state = State::Paused;
                self.save(&job)?;
            }
        }
        Ok(())
    }
    pub fn enqueue(&self, intent: Intent) -> Result<i64> {
        self.enqueue_as(intent, State::Queued)
    }
    pub fn enqueue_paused(&self, intent: Intent) -> Result<i64> {
        self.enqueue_as(intent, State::Paused)
    }
    fn enqueue_as(&self, intent: Intent, state: State) -> Result<i64> {
        intent.validate()?;
        self.check_destination(&intent.relative)?;
        let db = self.db.lock().unwrap();
        let mut job = Job {
            id: 0,
            key: intent.key.clone(),
            state,
            bytes: 0,
            relative_path: intent.relative.clone(),
            error: None,
            group_id: None,
            group_label: None,
            group_position: None,
            intent,
            validator: None,
            total: None,
            publication: None,
        };
        let tx = db.unchecked_transaction()?;
        tx.execute("INSERT INTO jobs(payload) VALUES ('')", [])?;
        job.id = tx.last_insert_rowid();
        tx.execute(
            "UPDATE jobs SET payload=? WHERE id=?",
            rusqlite::params![serde_json::to_string(&job)?, job.id],
        )?;
        tx.commit()?;
        self.wake.notify_one();
        Ok(job.id)
    }
    /// Validate the complete group before one transaction inserts any paused child.
    pub fn enqueue_group_paused(
        &self,
        id: &str,
        label: &str,
        intents: Vec<(u32, Intent)>,
    ) -> Result<Vec<i64>> {
        if !valid_group(id, label) || intents.is_empty() || intents.len() > 100 {
            return Err(Error::Invalid("invalid bounded download group".into()));
        }
        let mut previous = None;
        for (position, intent) in &intents {
            if previous.is_some_and(|n| *position <= n) {
                return Err(Error::Invalid("group positions must increase".into()));
            }
            previous = Some(*position);
            intent.validate()?;
            self.check_destination(&intent.relative)?;
        }
        let db = self.db.lock().unwrap();
        let tx = db.unchecked_transaction()?;
        let mut ids = Vec::new();
        for (position, intent) in intents {
            tx.execute("INSERT INTO jobs(payload) VALUES ('')", [])?;
            let job_id = tx.last_insert_rowid();
            let job = Job {
                id: job_id,
                key: intent.key.clone(),
                state: State::Paused,
                bytes: 0,
                relative_path: intent.relative.clone(),
                error: None,
                intent,
                validator: None,
                total: None,
                publication: None,
                group_id: Some(id.into()),
                group_label: Some(label.into()),
                group_position: Some(position),
            };
            tx.execute(
                "UPDATE jobs SET payload=? WHERE id=?",
                rusqlite::params![serde_json::to_string(&job)?, job_id],
            )?;
            ids.push(job_id);
        }
        tx.commit()?;
        Ok(ids)
    }
    pub fn jobs(&self) -> Result<Vec<Job>> {
        let db = self.db.lock().unwrap();
        let mut stmt = db.prepare("SELECT id, payload FROM jobs ORDER BY id")?;
        let rows = stmt.query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, String>(1)?)))?;
        rows.map(|r| {
            let (id, text) = r?;
            let job: Job = serde_json::from_str(&text)?;
            job.intent.validate()?;
            crate::files::relative(&job.relative_path)?;
            self.check_destination(&job.relative_path)?;
            if job.id != id
                || id <= 0
                || job.key != job.intent.key
                || job.bytes > i64::MAX as u64
                || job.total.is_some_and(|n| n > i64::MAX as u64)
            {
                return Err(Error::Invalid("inconsistent persisted job".into()));
            }
            match (&job.group_id, &job.group_label, job.group_position) {
                (None, None, None) => {}
                (Some(id), Some(label), Some(_)) if valid_group(id, label) => {}
                _ => return Err(Error::Invalid("inconsistent persisted group".into())),
            }
            if let Some(v) = &job.validator {
                let valid = if let Some(etag) = v.strip_prefix("etag:") {
                    let mut headers = reqwest::header::HeaderMap::new();
                    if let Ok(value) = reqwest::header::HeaderValue::from_str(etag) {
                        headers.insert(reqwest::header::ETAG, value);
                    }
                    crate::worker::validator(&headers).as_ref() == Some(v)
                } else if let Some(date) = v.strip_prefix("date:") {
                    httpdate::parse_http_date(date).is_ok()
                } else {
                    false
                };
                if !valid {
                    return Err(Error::Invalid("invalid persisted validator".into()));
                }
            }
            Ok(job)
        })
        .collect()
    }
    pub(crate) fn check_destination(&self, relative: &str) -> Result<()> {
        if self.reserved_paths.iter().any(|p| {
            p.eq_ignore_ascii_case(relative)
                || relative
                    .to_ascii_lowercase()
                    .starts_with(&format!("{}/", p.to_ascii_lowercase()))
        }) {
            return Err(Error::Invalid(
                "destination collides with job database".into(),
            ));
        }
        Ok(())
    }
}
