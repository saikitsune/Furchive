//! Capability-relative filesystem adapter. Never resolve-then-open media paths.
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::{fs::File, io, path::Path};

#[cfg(any(target_os = "linux", target_os = "macos"))]
#[path = "files/unix.rs"]
mod platform;
#[cfg(windows)]
#[path = "files/windows.rs"]
mod platform;
pub(crate) use platform::{identity, sync_directory};

/// A receipt must retain the whole native identity, not a truncated Windows ID.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) enum FileIdentity {
    Unix { device: u64, inode: u64 },
    Windows { volume: u64, file: [u8; 16] },
}
#[derive(Clone, Copy)]
enum Access {
    Read,
    PublicationRead,
    Create,
    Exclusive,
}

pub(crate) struct Grant {
    pub root: File,
    pub stage: File,
    _lock: File,
    pub identity: String,
}

pub(crate) fn relative(path: &str) -> Result<()> {
    if path.is_empty()
        || path.contains('\\')
        || path.split('/').any(|s| {
            s.is_empty()
                || s == "."
                || s == ".."
                || s.eq_ignore_ascii_case(".furchive-downloads")
                || s.to_ascii_lowercase().starts_with(".furchive-metadata-")
                || s.contains(':')
                || s.chars().any(char::is_control)
        })
        || Path::new(path).is_absolute()
    {
        return Err(Error::Invalid(
            "unsafe relative path or reserved staging name".into(),
        ));
    }
    let portable = furchive_domain::FilenameTemplate::parse(path)
        .and_then(|template| template.render(&furchive_domain::TemplateValues::default()))
        .map_err(|e| Error::Invalid(e.to_string()))?;
    if portable != path.replace(' ', "_") {
        return Err(Error::Invalid("noncanonical portable path".into()));
    }
    Ok(())
}

#[cfg(any(windows, target_os = "macos"))]
pub(crate) fn database_outside_grant(path: &Path, grant: &Grant) -> Result<()> {
    let full = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir()?.join(path)
    };
    let parent = full
        .parent()
        .ok_or_else(|| Error::Invalid("database needs parent directory".into()))?;
    let root_id = identity(&grant.root)?;
    // Trusted application-state pathname, opened nofollow by the same root walker.
    // Compare actual objects to cover native aliases, not canonicalized strings.
    for ancestor in parent.ancestors() {
        if identity(&platform::root(ancestor)?)? == root_id {
            return Err(Error::Invalid(
                "Windows/macOS require database outside granted media root (native aliases)".into(),
            ));
        }
    }
    Ok(())
}

impl Grant {
    pub fn metadata_read(&self, dir: &File, name: &str) -> Result<File> {
        Ok(platform::file(dir, name, Access::Read)?)
    }
    pub fn metadata_exists(&self, dir: &File, name: &str) -> Result<bool> {
        Ok(platform::exists(dir, name)?.is_some())
    }
    pub fn match_metadata(&self, dir: &File, name: &str, bytes: &[u8]) -> Result<FileIdentity> {
        use std::io::Read;
        let file = platform::file(dir, name, Access::Read)?;
        if !file.metadata()?.is_file() || file.metadata()?.len() != bytes.len() as u64 {
            return Err(Error::Invalid(
                "existing metadata differs; preserved".into(),
            ));
        }
        let mut existing = Vec::new();
        let identity = identity(&file)?;
        file.take(crate::METADATA_LIMIT as u64 + 1)
            .read_to_end(&mut existing)?;
        if existing != bytes {
            return Err(Error::Invalid(
                "existing metadata differs; preserved".into(),
            ));
        }
        Ok(identity)
    }
    pub fn metadata_stage(
        &self,
        dir: &File,
        name: &str,
        receipt: Option<&crate::model::Publication>,
        bytes: &[u8],
    ) -> Result<File> {
        use std::io::{Read, Seek, SeekFrom};
        let mut file = match if receipt.is_some() {
            Err(io::Error::from(io::ErrorKind::AlreadyExists))
        } else {
            platform::file(dir, name, Access::Exclusive)
        } {
            Ok(file) => file,
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {
                let (expected, digest) = receipt
                    .cloned()
                    .ok_or_else(|| Error::Invalid("unclaimed metadata stage; preserved".into()))?
                    .parts();
                use sha2::Digest;
                if digest != format!("{:x}", sha2::Sha256::digest(bytes)) {
                    return Err(Error::Invalid("metadata stage receipt mismatch".into()));
                }
                let mut file = platform::file(dir, name, Access::PublicationRead)?;
                if !platform::private(&file)? || identity(&file)? != expected {
                    return Err(Error::Invalid("metadata stage identity changed".into()));
                }
                let mut old = Vec::new();
                (&mut file)
                    .take(crate::METADATA_LIMIT as u64 + 1)
                    .read_to_end(&mut old)?;
                if old != bytes {
                    return Err(Error::Invalid("metadata stage bytes changed".into()));
                }
                file
            }
            Err(e) => return Err(e.into()),
        };
        if !platform::private(&file)? {
            return Err(Error::Invalid(
                "metadata stage is not private regular file".into(),
            ));
        }
        file.seek(SeekFrom::Start(0))?;
        sync_directory(dir)?;
        Ok(file)
    }
    pub fn publish_metadata(&self, dir: &File, temp: &str, file: &File, name: &str) -> Result<()> {
        let source = platform::file(dir, temp, Access::Read)?;
        if !platform::private(&source)? || identity(&source)? != identity(file)? {
            return Err(Error::Invalid("metadata stage namespace changed".into()));
        }
        match platform::publish(dir, temp, file, dir, name, false) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => Err(Error::Invalid(
                "metadata collision; existing file preserved".into(),
            )),
            Err(e) => Err(e.into()),
        }
    }
    pub fn open(path: &Path) -> Result<Self> {
        let root = platform::root(path)?;
        // Probe before creating staging or opening the database. Unsupported sync is
        // an error, never a best-effort success. Native tests must exercise it.
        sync_directory(&root)?;
        let identity = match identity(&root)? {
            FileIdentity::Unix { device, inode } => format!("{device}:{inode}"),
            FileIdentity::Windows { volume, file } => format!("windows:{volume}:{file:02x?}"),
        };
        match platform::mkdir(&root, ".furchive-downloads", true) {
            Ok(()) => sync_directory(&root)?,
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
            Err(e) => return Err(e.into()),
        }
        let stage = platform::directory(&root, ".furchive-downloads")?;
        let lock = platform::file(&stage, "owner.lock", Access::Create)?;
        if !platform::private(&lock)? {
            return Err(Error::Invalid("invalid ownership lock".into()));
        }
        lock.try_lock().map_err(|_| Error::Busy)?;
        Ok(Self {
            root,
            stage,
            _lock: lock,
            identity,
        })
    }
    pub fn bind_database(&self, identity: &str) -> Result<()> {
        use std::io::{Read, Write};
        match platform::file(&self.stage, "database.identity", Access::Exclusive) {
            Ok(mut marker) => {
                marker.write_all(identity.as_bytes())?;
                marker.sync_all()?;
                sync_directory(&self.stage)?;
            }
            Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {
                let marker = platform::file(&self.stage, "database.identity", Access::Read)?;
                if !platform::private(&marker)? {
                    return Err(Error::Invalid("invalid database marker".into()));
                }
                let mut text = String::new();
                marker.take(256).read_to_string(&mut text)?;
                if text != identity {
                    return Err(Error::Invalid(
                        "root staging belongs to another database".into(),
                    ));
                }
            }
            Err(e) => return Err(e.into()),
        }
        Ok(())
    }
    pub fn part(&self, id: i64) -> Result<File> {
        let file = platform::file(&self.stage, &format!("{id}.part"), Access::Create)?;
        if !platform::private(&file)? {
            return Err(Error::Invalid("part must be private regular file".into()));
        }
        sync_directory(&self.stage)?;
        Ok(file)
    }
    pub fn part_exists(&self, id: i64) -> Result<bool> {
        Ok(platform::exists(&self.stage, &format!("{id}.part"))?.is_some())
    }
    pub fn remove_part(&self, id: i64) -> Result<()> {
        match platform::unlink(&self.stage, &format!("{id}.part")) {
            Ok(()) => {
                sync_directory(&self.stage)?;
                Ok(())
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(e.into()),
        }
    }
    pub fn exists(&self, path: &str) -> Result<bool> {
        let (parent, name) = self.parent(path)?;
        match platform::exists(&parent, &name)? {
            Some(true) => Ok(true),
            None => Ok(false),
            Some(false) => Err(Error::Invalid("destination is not a regular file".into())),
        }
    }
    pub fn destination(&self, path: &str) -> Result<File> {
        let (parent, name) = self.read_parent(path)?;
        let file = platform::file(&parent, &name, Access::Read)?;
        if !file.metadata()?.is_file() {
            return Err(Error::Invalid("destination is not regular".into()));
        }
        Ok(file)
    }
    pub fn read_parent(&self, path: &str) -> Result<(File, String)> {
        relative(path)?;
        // Read/retry/reconciliation never creates missing ancestors.
        let (parents, name) = path.rsplit_once('/').unwrap_or(("", path));
        let mut parent = self.root.try_clone()?;
        if !parents.is_empty() {
            let mut prefix = String::new();
            for piece in parents.split('/') {
                if !prefix.is_empty() {
                    prefix.push('/');
                }
                prefix.push_str(piece);
                parent = platform::directory(&self.root, &prefix)?;
                self.ensure_media_directory(&parent)?;
            }
        }
        self.ensure_media_directory(&parent)?;
        Ok((parent, name.to_owned()))
    }
    pub fn ensure_media_directory(&self, dir: &File) -> Result<()> {
        if identity(dir)? == identity(&self.stage)? {
            return Err(Error::Invalid(
                "destination aliases private staging directory".into(),
            ));
        }
        Ok(())
    }
    pub fn parent(&self, path: &str) -> Result<(File, String)> {
        relative(path)?;
        let mut pieces: Vec<_> = path.split('/').collect();
        let name = pieces.pop().unwrap().to_owned();
        let mut prefix = String::new();
        let mut dir = self.root.try_clone()?;
        for piece in pieces {
            match platform::mkdir(&dir, piece, false) {
                Ok(()) => sync_directory(&dir)?,
                Err(e) if e.kind() == io::ErrorKind::AlreadyExists => {}
                Err(e) => return Err(e.into()),
            }
            if !prefix.is_empty() {
                prefix.push('/');
            }
            prefix.push_str(piece);
            dir = platform::directory(&self.root, &prefix)?;
            self.ensure_media_directory(&dir)?;
        }
        Ok((dir, name))
    }
    pub fn publish(
        &self,
        id: i64,
        part: &File,
        dir: &File,
        name: &str,
        overwrite: bool,
    ) -> io::Result<()> {
        platform::publish(
            &self.stage,
            &format!("{id}.part"),
            part,
            dir,
            name,
            overwrite,
        )
    }
}
