//! Windows NT handle-relative backend (local NTFS only).
//! No Win32 multi-component media opens; every component rejects all reparse tags.
//! FFI layouts follow winternl.h / ntifs.h. Calls are synchronous and handles RAII.
use super::{Access, FileIdentity};
use std::{
    ffi::c_void,
    fs::File,
    io,
    mem::{offset_of, size_of},
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle},
    },
    path::{Component, Path, Prefix},
    ptr,
};

type Handle = *mut c_void;
#[repr(C)]
struct UnicodeString {
    length: u16,
    maximum_length: u16,
    buffer: *mut u16,
}
#[repr(C)]
struct ObjectAttributes {
    length: u32,
    root: Handle,
    name: *mut UnicodeString,
    attributes: u32,
    security: *mut c_void,
    quality: *mut c_void,
}
#[repr(C)]
#[derive(Default)]
struct IoStatus {
    status: usize,
    information: usize,
}
#[repr(C)]
struct FileIdInfo {
    volume: u64,
    id: [u8; 16],
}
#[repr(C)]
struct AttributeTag {
    attributes: u32,
    tag: u32,
}
#[repr(C)]
struct FileStandardInfo {
    allocation: i64,
    end: i64,
    links: u32,
    delete_pending: u8,
    directory: u8,
}
#[repr(C)]
struct RenameInfo {
    replace: u32,
    root: Handle,
    length: u32,
    name: [u16; 1],
}

#[link(name = "ntdll")]
unsafe extern "system" {
    fn NtCreateFile(
        handle: *mut Handle,
        access: u32,
        object: *mut ObjectAttributes,
        status: *mut IoStatus,
        allocation: *const i64,
        attributes: u32,
        share: u32,
        disposition: u32,
        options: u32,
        ea: *const c_void,
        ea_length: u32,
    ) -> i32;
    fn NtSetInformationFile(
        handle: Handle,
        status: *mut IoStatus,
        data: *const c_void,
        length: u32,
        class: u32,
    ) -> i32;
    fn NtFlushBuffersFile(handle: Handle, status: *mut IoStatus) -> i32;
    fn RtlNtStatusToDosError(status: i32) -> u32;
}
#[link(name = "kernel32")]
unsafe extern "system" {
    fn GetFileInformationByHandleEx(
        handle: Handle,
        class: u32,
        data: *mut c_void,
        length: u32,
    ) -> i32;
    fn GetVolumeInformationByHandleW(
        handle: Handle,
        volume: *mut u16,
        volume_len: u32,
        serial: *mut u32,
        max_component: *mut u32,
        flags: *mut u32,
        fs: *mut u16,
        fs_len: u32,
    ) -> i32;
    fn LocalFree(memory: *mut c_void) -> *mut c_void;
}
#[link(name = "advapi32")]
unsafe extern "system" {
    fn ConvertStringSecurityDescriptorToSecurityDescriptorW(
        text: *const u16,
        revision: u32,
        descriptor: *mut *mut c_void,
        size: *mut u32,
    ) -> i32;
}
const READ_ATTRIBUTES: u32 = 0x80;
const SYNCHRONIZE: u32 = 0x100000;
const DELETE: u32 = 0x10000;
const DIRECTORY: u32 = 1;
const NON_DIRECTORY: u32 = 0x40;
const OPEN_REPARSE: u32 = 0x200000;
const SYNC_NONALERT: u32 = 0x20;
const OPEN: u32 = 1;
const CREATE: u32 = 2;
const OPEN_IF: u32 = 3;
const REPARSE_ATTRIBUTE: u32 = 0x400;

fn status(code: i32) -> io::Result<()> {
    if code >= 0 {
        Ok(())
    } else {
        // SAFETY: pure status conversion; no pointers.
        Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(code) } as i32,
        ))
    }
}
fn unsupported(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::Unsupported, message)
}

struct Security(*mut c_void);
impl Security {
    fn private() -> io::Result<Self> {
        // Protected DACL: owner-rights and SYSTEM only. Owner is assigned by OS
        // from the creating token. Inheritance cannot broaden newly created parts.
        let sddl: Vec<u16> = "D:P(A;;FA;;;OW)(A;;FA;;;SY)\0".encode_utf16().collect();
        let mut descriptor = ptr::null_mut();
        // SAFETY: terminated input; API allocates descriptor released by Drop.
        if unsafe {
            ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.as_ptr(),
                1,
                &mut descriptor,
                ptr::null_mut(),
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(Self(descriptor))
    }
}
impl Drop for Security {
    fn drop(&mut self) {
        // SAFETY: allocation returned by ConvertStringSecurityDescriptor... only.
        unsafe {
            LocalFree(self.0);
        }
    }
}

fn open(
    parent: Option<&File>,
    name: &[u16],
    access: u32,
    disposition: u32,
    kind: u32,
    private: bool,
) -> io::Result<File> {
    let bytes = name
        .len()
        .checked_mul(2)
        .and_then(|n| u16::try_from(n).ok())
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "NT name too long"))?;
    if name.contains(&0) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "NUL in NT name",
        ));
    }
    let security = if private {
        Some(Security::private()?)
    } else {
        None
    };
    let mut name = name.to_vec();
    let mut unicode = UnicodeString {
        length: bytes,
        maximum_length: bytes,
        buffer: name.as_mut_ptr(),
    };
    let mut object = ObjectAttributes {
        length: size_of::<ObjectAttributes>() as u32,
        root: parent.map_or(ptr::null_mut(), |p| p.as_raw_handle()),
        name: &mut unicode,
        // OBJ_CASE_INSENSITIVE; FILE_OPEN_REPARSE_POINT below stops leaf traversal.
        attributes: 0x40,
        security: security.as_ref().map_or(ptr::null_mut(), |s| s.0),
        quality: ptr::null_mut(),
    };
    let mut handle = ptr::null_mut();
    let mut result = IoStatus::default();
    // SAFETY: all buffers/structures remain live for this synchronous call.
    status(unsafe {
        NtCreateFile(
            &mut handle,
            access | READ_ATTRIBUTES | SYNCHRONIZE,
            &mut object,
            &mut result,
            ptr::null(),
            0x80,
            7,
            disposition,
            kind | OPEN_REPARSE | SYNC_NONALERT,
            ptr::null(),
            0,
        )
    })?;
    // SAFETY: successful NtCreateFile returns a newly owned handle.
    let file = unsafe { File::from_raw_handle(handle) };
    reject_reparse(&file)?;
    Ok(file)
}
fn component(name: &str) -> io::Result<Vec<u16>> {
    if name.is_empty() || name == "." || name == ".." || name.contains(['/', '\\', ':', '\0']) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid NT relative component",
        ));
    }
    Ok(name.encode_utf16().collect())
}
fn query<T>(file: &File, class: u32) -> io::Result<T> {
    let mut data = std::mem::MaybeUninit::<T>::zeroed();
    // SAFETY: T is a native fixed-size information structure at the callers below.
    if unsafe {
        GetFileInformationByHandleEx(
            file.as_raw_handle(),
            class,
            data.as_mut_ptr().cast(),
            size_of::<T>() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: API success initialized the full information structure.
    Ok(unsafe { data.assume_init() })
}
fn reject_reparse(file: &File) -> io::Result<()> {
    let tag: AttributeTag = query(file, 9)?; // FileAttributeTagInfo
    if tag.attributes & REPARSE_ATTRIBUTE != 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "reparse point rejected (symlink/junction/cloud/mount)",
        ));
    }
    Ok(())
}
fn ntfs(file: &File) -> io::Result<()> {
    let mut name = [0u16; 32];
    // SAFETY: output buffer is writable, optional outputs are null.
    if unsafe {
        GetVolumeInformationByHandleW(
            file.as_raw_handle(),
            ptr::null_mut(),
            0,
            ptr::null_mut(),
            ptr::null_mut(),
            ptr::null_mut(),
            name.as_mut_ptr(),
            name.len() as u32,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    let end = name.iter().position(|x| *x == 0).unwrap_or(name.len());
    if String::from_utf16_lossy(&name[..end]) != "NTFS" {
        return Err(unsupported(
            "durable directory publication requires local NTFS",
        ));
    }
    Ok(())
}
pub(super) fn root(path: &Path) -> io::Result<File> {
    let full = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir()?.join(path)
    };
    let mut components = full.components();
    let drive = match components.next() {
        Some(Component::Prefix(p)) => match p.kind() {
            Prefix::Disk(d) | Prefix::VerbatimDisk(d) => d,
            _ => {
                return Err(unsupported(
                    "only local drive-letter grants supported; UNC/device roots rejected",
                ))
            }
        },
        _ => return Err(unsupported("grant requires absolute local drive")),
    };
    if components.next() != Some(Component::RootDir) {
        return Err(unsupported("drive-relative grant rejected"));
    }
    let name: Vec<u16> = format!("\\??\\{}:\\", drive as char)
        .encode_utf16()
        .collect();
    let mut dir = open(None, &name, 0x20, OPEN, DIRECTORY, false)?; // FILE_TRAVERSE
    ntfs(&dir)?;
    for piece in components {
        match piece {
            Component::Normal(name) => {
                let name: Vec<u16> = name.encode_wide().collect();
                if name.contains(&(b':' as u16)) {
                    return Err(unsupported("NT stream name in grant"));
                }
                dir = open(Some(&dir), &name, 0x20, OPEN, DIRECTORY, false)?;
            }
            _ => return Err(unsupported("grant must contain only normal components")),
        }
    }
    Ok(dir)
}
pub(super) fn directory(root: &File, path: &str) -> io::Result<File> {
    let mut dir = root.try_clone()?;
    for name in path.split('/') {
        dir = open(Some(&dir), &component(name)?, 0x20, OPEN, DIRECTORY, false)?;
    }
    Ok(dir)
}
pub(super) fn mkdir(dir: &File, name: &str, private: bool) -> io::Result<()> {
    // FILE_CREATE fails on existing entry, including reparse points.
    open(
        Some(dir),
        &component(name)?,
        0x20,
        CREATE,
        DIRECTORY,
        private,
    )
    .map(drop)
}
pub(super) fn file(dir: &File, name: &str, access: Access) -> io::Result<File> {
    let (access, disposition, private) = match access {
        Access::Read => (1, OPEN, false),
        Access::Create => (1 | 2 | DELETE, OPEN_IF, true),
        Access::Exclusive => (1 | 2, CREATE, true),
    };
    open(
        Some(dir),
        &component(name)?,
        access,
        disposition,
        NON_DIRECTORY,
        private,
    )
}
pub(super) fn private(file: &File) -> io::Result<bool> {
    reject_reparse(file)?;
    let info: FileStandardInfo = query(file, 1)?;
    Ok(info.directory == 0 && info.links == 1 && info.delete_pending == 0)
}
pub(super) fn exists(dir: &File, name: &str) -> io::Result<Option<bool>> {
    match open(Some(dir), &component(name)?, 0, OPEN, 0, false) {
        Ok(file) => Ok(Some(file.metadata()?.is_file())),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e),
    }
}
pub(super) fn unlink(dir: &File, name: &str) -> io::Result<()> {
    let file = open(
        Some(dir),
        &component(name)?,
        DELETE,
        OPEN,
        NON_DIRECTORY,
        false,
    )?;
    let delete = 1u8; // FileDispositionInformation, BOOLEAN DeleteFile
    let mut ios = IoStatus::default();
    // SAFETY: valid owned handle, fixed-size BOOLEAN input, synchronous call.
    status(unsafe {
        NtSetInformationFile(
            file.as_raw_handle(),
            &mut ios,
            (&delete as *const u8).cast(),
            1,
            13,
        )
    })
}
pub(crate) fn identity(file: &File) -> io::Result<FileIdentity> {
    let info: FileIdInfo = query(file, 18)?; // FileIdInfo: full 128-bit native ID
    Ok(FileIdentity::Windows {
        volume: info.volume,
        file: info.id,
    })
}
pub(crate) fn sync_directory(dir: &File) -> io::Result<()> {
    ntfs(dir)?;
    // Empty relative name reopens the exact pinned directory, not its pathname.
    // FILE_ADD_FILE/FILE_ADD_SUBDIRECTORY are directory write access aliases.
    // MS-FSA2.1.5.7+footnote80 supplies the NTFS namespace flush contract.
    // Actual user-mode reopen/flush availability is a mandatory native gate.
    // Do not assume that read-only File::sync_all can flush a Windows directory.
    let writable = open(Some(dir), &[], 2 | 4, OPEN, DIRECTORY, false).map_err(|e| {
        io::Error::new(
            e.kind(),
            format!("NTFS directory flush access unavailable: {e}"),
        )
    })?;
    if identity(&writable)? != identity(dir)? {
        return Err(unsupported("directory reopen identity changed"));
    }
    let mut ios = IoStatus::default();
    // SAFETY: writable synchronous directory handle and valid output structure.
    status(unsafe { NtFlushBuffersFile(writable.as_raw_handle(), &mut ios) }).map_err(|e| {
        io::Error::new(
            e.kind(),
            format!("NTFS directory flush unsupported/failed: {e}"),
        )
    })
}
pub(super) fn publish(
    _stage: &File,
    _source: &str,
    part: &File,
    dir: &File,
    name: &str,
    overwrite: bool,
) -> io::Result<()> {
    let name = component(name)?;
    let length = offset_of!(RenameInfo, name) + name.len() * 2;
    // u64 storage provides native alignment; include trailing structure padding.
    let mut buffer = vec![0u64; (length.max(size_of::<RenameInfo>())).div_ceil(8)];
    let pointer = buffer.as_mut_ptr().cast::<RenameInfo>();
    // SAFETY: aligned variable-length buffer holds header and all UTF16 units.
    unsafe {
        (*pointer).replace = if overwrite { 1 | 2 } else { 0 }; // REPLACE_IF_EXISTS | POSIX_SEMANTICS
        (*pointer).root = dir.as_raw_handle();
        (*pointer).length = (name.len() * 2) as u32;
        ptr::copy_nonoverlapping(
            name.as_ptr(),
            ptr::addr_of_mut!((*pointer).name).cast::<u16>(),
            name.len(),
        );
    }
    let mut ios = IoStatus::default();
    // FileRenameInformationEx (65): atomic handle-relative rename, no copy/delete
    // or pathname fallback. Old Windows/unsupported FS must fail, not emulate.
    // SAFETY: buffer and handles live for synchronous call.
    status(unsafe {
        NtSetInformationFile(
            part.as_raw_handle(),
            &mut ios,
            pointer.cast(),
            (buffer.len() * 8) as u32,
            65,
        )
    })
}
