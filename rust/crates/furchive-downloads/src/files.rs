//! Linux dirfd-relative I/O. No path canonicalize-then-open checks.
use crate::{Error, Result};
use rustix::fs::{self, AtFlags, Mode, OFlags, ResolveFlags};
use std::{
    fs::File,
    path::{Component, Path},
};

pub(crate) struct Grant {
    pub root: File,
    pub stage: File,
    _lock: File,
    pub identity: String,
}
fn io(e: rustix::io::Errno) -> Error {
    Error::Io(e.into())
}
fn flags() -> OFlags {
    OFlags::RDONLY | OFlags::DIRECTORY | OFlags::NOFOLLOW | OFlags::CLOEXEC
}
pub(crate) fn relative(path: &str) -> Result<()> {
    if path.is_empty()
        || path.contains('\\')
        || path.split('/').any(|s| {
            s.is_empty()
                || s == "."
                || s == ".."
                || s == ".furchive-downloads"
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
impl Grant {
    pub fn open(path: &Path) -> Result<Self> {
        let mut dir = File::from(fs::open("/", flags(), Mode::empty()).map_err(io)?);
        let full = if path.is_absolute() {
            path.to_path_buf()
        } else {
            std::env::current_dir()?.join(path)
        };
        for component in full.components() {
            match component {
                Component::RootDir => {}
                Component::Normal(name) => {
                    dir = File::from(fs::openat(&dir, name, flags(), Mode::empty()).map_err(io)?)
                }
                _ => {
                    return Err(Error::Invalid(
                        "root must have only normal components".into(),
                    ))
                }
            }
        }
        let stat = fs::fstat(&dir).map_err(io)?;
        let identity = format!("{}:{}", stat.st_dev, stat.st_ino);
        match fs::mkdirat(&dir, ".furchive-downloads", Mode::from_raw_mode(0o700)) {
            Ok(()) => {}
            Err(e) if e == rustix::io::Errno::EXIST => {}
            Err(e) => return Err(io(e)),
        }
        let stage = File::from(
            fs::openat2(
                &dir,
                ".furchive-downloads",
                flags(),
                Mode::empty(),
                ResolveFlags::BENEATH | ResolveFlags::NO_SYMLINKS | ResolveFlags::NO_MAGICLINKS,
            )
            .map_err(io)?,
        );
        dir.sync_all()?;
        let lock = File::from(
            fs::openat(
                &stage,
                "owner.lock",
                OFlags::CREATE
                    | OFlags::RDWR
                    | OFlags::NOFOLLOW
                    | OFlags::CLOEXEC
                    | OFlags::NONBLOCK,
                Mode::from_raw_mode(0o600),
            )
            .map_err(io)?,
        );
        if !lock.metadata()?.is_file() {
            return Err(Error::Invalid("invalid ownership lock".into()));
        }
        lock.try_lock().map_err(|_| Error::Busy)?;
        Ok(Self {
            root: dir,
            stage,
            _lock: lock,
            identity,
        })
    }
    pub fn bind_database(&self, identity: &str) -> Result<()> {
        use std::io::{Read, Write};
        let opened = fs::openat(
            &self.stage,
            "database.identity",
            OFlags::CREATE | OFlags::EXCL | OFlags::WRONLY | OFlags::NOFOLLOW | OFlags::CLOEXEC,
            Mode::from_raw_mode(0o600),
        );
        match opened {
            Ok(fd) => {
                let mut marker = File::from(fd);
                marker.write_all(identity.as_bytes())?;
                marker.sync_all()?;
                self.stage.sync_all()?;
            }
            Err(e) if e == rustix::io::Errno::EXIST => {
                let marker = File::from(
                    fs::openat(
                        &self.stage,
                        "database.identity",
                        OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::NONBLOCK | OFlags::CLOEXEC,
                        Mode::empty(),
                    )
                    .map_err(io)?,
                );
                if !marker.metadata()?.is_file() {
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
            Err(e) => return Err(io(e)),
        }
        Ok(())
    }
    pub fn part(&self, id: i64) -> Result<File> {
        let file = File::from(
            fs::openat(
                &self.stage,
                format!("{id}.part"),
                OFlags::CREATE
                    | OFlags::RDWR
                    | OFlags::NOFOLLOW
                    | OFlags::CLOEXEC
                    | OFlags::NONBLOCK,
                Mode::from_raw_mode(0o600),
            )
            .map_err(io)?,
        );
        let meta = file.metadata()?;
        use std::os::unix::fs::MetadataExt;
        if !meta.is_file() || meta.nlink() != 1 {
            return Err(Error::Invalid("part must be private regular file".into()));
        }
        self.stage.sync_all()?;
        Ok(file)
    }
    pub fn part_exists(&self, id: i64) -> Result<bool> {
        match fs::statat(&self.stage, format!("{id}.part"), AtFlags::SYMLINK_NOFOLLOW) {
            Ok(_) => Ok(true),
            Err(e) if e == rustix::io::Errno::NOENT => Ok(false),
            Err(e) => Err(io(e)),
        }
    }
    pub fn remove_part(&self, id: i64) -> Result<()> {
        match fs::unlinkat(&self.stage, format!("{id}.part"), AtFlags::empty()) {
            Ok(()) => {
                self.stage.sync_all()?;
                Ok(())
            }
            Err(e) if e == rustix::io::Errno::NOENT => Ok(()),
            Err(e) => Err(io(e)),
        }
    }
    pub fn exists(&self, path: &str) -> Result<bool> {
        let (dir, name) = self.parent(path)?;
        match fs::statat(&dir, &name, AtFlags::SYMLINK_NOFOLLOW) {
            Ok(stat) => {
                if fs::FileType::from_raw_mode(stat.st_mode) != fs::FileType::RegularFile {
                    return Err(Error::Invalid("destination is not a regular file".into()));
                }
                Ok(true)
            }
            Err(e) if e == rustix::io::Errno::NOENT => Ok(false),
            Err(e) => Err(io(e)),
        }
    }
    pub fn destination(&self, path: &str) -> Result<File> {
        relative(path)?;
        let file = File::from(
            fs::openat2(
                &self.root,
                path,
                OFlags::RDONLY | OFlags::NOFOLLOW | OFlags::NONBLOCK | OFlags::CLOEXEC,
                Mode::empty(),
                ResolveFlags::BENEATH | ResolveFlags::NO_SYMLINKS | ResolveFlags::NO_MAGICLINKS,
            )
            .map_err(io)?,
        );
        if !file.metadata()?.is_file() {
            return Err(Error::Invalid("destination is not regular".into()));
        }
        Ok(file)
    }
    pub fn parent(&self, path: &str) -> Result<(File, String)> {
        relative(path)?;
        let mut pieces: Vec<_> = path.split('/').collect();
        let name = pieces.pop().unwrap().to_owned();
        let mut prefix = String::new();
        let mut dir = self.root.try_clone()?;
        for piece in pieces {
            match fs::mkdirat(&dir, piece, Mode::from_raw_mode(0o755)) {
                Ok(()) => dir.sync_all()?,
                Err(e) if e == rustix::io::Errno::EXIST => {}
                Err(e) => return Err(io(e)),
            }
            if !prefix.is_empty() {
                prefix.push('/');
            }
            prefix.push_str(piece);
            dir = File::from(
                fs::openat2(
                    &self.root,
                    &prefix,
                    flags(),
                    Mode::empty(),
                    ResolveFlags::BENEATH | ResolveFlags::NO_SYMLINKS | ResolveFlags::NO_MAGICLINKS,
                )
                .map_err(io)?,
            );
        }
        Ok((dir, name))
    }
}
