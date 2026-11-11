//! Linux keeps openat2; macOS traverses single components with O_NOFOLLOW.
use super::{Access, FileIdentity};
use rustix::fs::{self, AtFlags, Mode, OFlags};
use std::{
    fs::File,
    io,
    os::unix::fs::MetadataExt,
    path::{Component, Path},
};

fn flags() -> OFlags {
    OFlags::RDONLY | OFlags::DIRECTORY | OFlags::NOFOLLOW | OFlags::CLOEXEC
}
pub(super) fn root(path: &Path) -> io::Result<File> {
    let full = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut dir = File::from(fs::open("/", flags(), Mode::empty())?);
    for component in full.components() {
        match component {
            Component::RootDir => {}
            Component::Normal(name) => {
                dir = File::from(fs::openat(&dir, name, flags(), Mode::empty())?)
            }
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "root must have only normal components",
                ))
            }
        }
    }
    Ok(dir)
}
pub(super) fn directory(root: &File, path: &str) -> io::Result<File> {
    #[cfg(target_os = "linux")]
    {
        use rustix::fs::ResolveFlags;
        Ok(File::from(fs::openat2(
            root,
            path,
            flags(),
            Mode::empty(),
            ResolveFlags::BENEATH | ResolveFlags::NO_SYMLINKS | ResolveFlags::NO_MAGICLINKS,
        )?))
    }
    #[cfg(target_os = "macos")]
    {
        let mut dir = root.try_clone()?;
        for piece in path.split('/') {
            if piece.is_empty() || piece == "." || piece == ".." {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "invalid directory component",
                ));
            }
            dir = File::from(fs::openat(&dir, piece, flags(), Mode::empty())?);
        }
        Ok(dir)
    }
}
pub(super) fn mkdir(dir: &File, name: &str, private: bool) -> io::Result<()> {
    Ok(fs::mkdirat(
        dir,
        name,
        Mode::from_raw_mode(if private { 0o700 } else { 0o755 }),
    )?)
}
pub(super) fn file(dir: &File, name: &str, access: Access) -> io::Result<File> {
    let access = match access {
        Access::Read => OFlags::RDONLY,
        Access::Create => OFlags::CREATE | OFlags::RDWR,
        Access::Exclusive => OFlags::CREATE | OFlags::EXCL | OFlags::WRONLY,
    };
    Ok(File::from(fs::openat(
        dir,
        name,
        access | OFlags::NOFOLLOW | OFlags::CLOEXEC | OFlags::NONBLOCK,
        Mode::from_raw_mode(0o600),
    )?))
}
pub(super) fn private(file: &File) -> io::Result<bool> {
    let meta = file.metadata()?;
    Ok(meta.is_file() && meta.nlink() == 1)
}
pub(super) fn exists(dir: &File, name: &str) -> io::Result<Option<bool>> {
    match fs::statat(dir, name, AtFlags::SYMLINK_NOFOLLOW) {
        Ok(stat) => Ok(Some(
            fs::FileType::from_raw_mode(stat.st_mode) == fs::FileType::RegularFile,
        )),
        Err(e) if e == rustix::io::Errno::NOENT => Ok(None),
        Err(e) => Err(e.into()),
    }
}
pub(super) fn unlink(dir: &File, name: &str) -> io::Result<()> {
    Ok(fs::unlinkat(dir, name, AtFlags::empty())?)
}
pub(crate) fn identity(file: &File) -> io::Result<FileIdentity> {
    let meta = file.metadata()?;
    Ok(FileIdentity::Unix {
        device: meta.dev(),
        inode: meta.ino(),
    })
}
pub(crate) fn sync_directory(dir: &File) -> io::Result<()> {
    // Propagate EINVAL/ENOTSUP: no success on filesystems without directory sync.
    dir.sync_all()
}
pub(super) fn publish(
    stage: &File,
    source: &str,
    _part: &File,
    dir: &File,
    name: &str,
    overwrite: bool,
) -> io::Result<()> {
    // On Darwin rustix uses renameatx_np(RENAME_EXCL), never a link/unlink emulation.
    let flags = if overwrite {
        fs::RenameFlags::empty()
    } else {
        fs::RenameFlags::NOREPLACE
    };
    Ok(fs::renameat_with(stage, source, dir, name, flags)?)
}
