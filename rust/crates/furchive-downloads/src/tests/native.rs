//! Extra native contracts; these are real fixtures, not cfg-certification stubs.
use super::*;

#[cfg(any(windows, target_os = "macos"))]
#[test]
fn native_database_inside_media_grant_fails_before_creating_database() {
    let temp = OwnedTemp::new();
    let db = temp.path().join("inside.db");
    let error = Engine::open(&db, temp.path()).err().unwrap().to_string();
    assert!(error.contains("outside granted media root"), "{error}");
    assert!(!db.exists());
    let engine = Engine::open(temp.database("db"), temp.path()).unwrap();
    assert!(engine.jobs().unwrap().is_empty());
}

#[test]
fn grant_identity_matches_reopened_object_and_differs_from_other_directory() {
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    let other = temp.path().join("other");
    std::fs::create_dir(&other).unwrap();
    let (parent, _) = grant.parent("other/file.jpg").unwrap();
    assert_eq!(
        crate::files::identity(&grant.root).unwrap(),
        crate::files::identity(&grant.root.try_clone().unwrap()).unwrap()
    );
    assert_ne!(
        crate::files::identity(&grant.root).unwrap(),
        crate::files::identity(&parent).unwrap()
    );
    crate::files::sync_directory(&parent).unwrap();
    crate::files::sync_directory(&grant.root).unwrap();
}

#[cfg(windows)]
#[test]
fn windows_junctions_in_root_ancestors_and_destination_parents_are_rejected() {
    let temp = OwnedTemp::new();
    let outside = temp.path().join("outside");
    std::fs::create_dir(&outside).unwrap();
    std::fs::write(outside.join("keep.jpg"), b"untouched").unwrap();
    let junction = temp.path().join("junction");
    let output = std::process::Command::new("cmd")
        .args(["/d", "/c", "mklink", "/J"])
        .arg(&junction)
        .arg(&outside)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "junction fixture failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(crate::files::Grant::open(&junction).is_err());
    std::fs::create_dir(outside.join("child")).unwrap();
    assert!(crate::files::Grant::open(&junction.join("child")).is_err());
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    assert!(grant.parent("junction/new.jpg").is_err());
    assert!(grant.destination("junction/keep.jpg").is_err());
    assert_eq!(
        std::fs::read(outside.join("keep.jpg")).unwrap(),
        b"untouched"
    );
    assert!(!outside.join("new.jpg").exists());
}

#[cfg(windows)]
#[test]
fn windows_directory_flush_rejects_regular_file_and_unc_root() {
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    let part = grant.part(7).unwrap();
    assert!(crate::files::sync_directory(&part).is_err());
    assert!(crate::files::Grant::open(std::path::Path::new(r"\\localhost\C$\")).is_err());
    assert!(matches!(
        crate::files::identity(&part).unwrap(),
        crate::files::FileIdentity::Windows { .. }
    ));
}

#[tokio::test]
async fn reconciliation_rejects_same_bytes_in_replaced_destination_object() {
    let fixture = fixture(vec![response("durable")]).await;
    let temp = OwnedTemp::new();
    let db = temp.database("db");
    let engine = std::sync::Arc::new(Engine::open_test(&db, temp.path(), &fixture.origin).unwrap());
    engine.enqueue(intent("1", DuplicatePolicy::Skip)).unwrap();
    engine
        .crash_at
        .store(2, std::sync::atomic::Ordering::SeqCst);
    let worker = engine.start().unwrap();
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            if engine.jobs().unwrap()[0].error.is_some() {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    worker.shutdown().await.unwrap();
    drop(engine);
    // Hold original open to prevent inode/file-id recycling; identical bytes are
    // not enough evidence for reconciling a different publication object.
    let original = std::fs::File::open(temp.path().join("1.jpg")).unwrap();
    std::fs::remove_file(temp.path().join("1.jpg")).unwrap();
    std::fs::write(temp.path().join("1.jpg"), b"durable").unwrap();
    assert!(Engine::open(&db, temp.path()).is_err());
    drop(original);
    assert_eq!(
        std::fs::read(temp.path().join("1.jpg")).unwrap(),
        b"durable"
    );
}

#[test]
fn invalid_root_with_parent_component_is_rejected_before_staging_creation() {
    let temp = OwnedTemp::new();
    std::fs::create_dir(temp.path().join("other")).unwrap();
    assert!(crate::files::Grant::open(&temp.path().join("other/../")).is_err());
    assert!(!temp.path().join(".furchive-downloads").exists());
}

#[test]
fn nonregular_part_and_hardlinked_marker_are_rejected_before_write() {
    let temp = OwnedTemp::new();
    let grant = crate::files::Grant::open(temp.path()).unwrap();
    std::fs::create_dir(temp.path().join(".furchive-downloads/8.part")).unwrap();
    assert!(grant.part(8).is_err());
    let original = temp.path().join("original");
    std::fs::write(&original, b"do not touch").unwrap();
    std::fs::hard_link(
        &original,
        temp.path().join(".furchive-downloads/database.identity"),
    )
    .unwrap();
    assert!(grant.bind_database("do not touch").is_err());
    assert_eq!(std::fs::read(original).unwrap(), b"do not touch");
}
