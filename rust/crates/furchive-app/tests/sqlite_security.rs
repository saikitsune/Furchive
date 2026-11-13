#[test]
fn bundled_sqlite_contains_the_mismatched_wal_page_size_memory_disclosure_fix() {
    // RUSTSEC-2026-0459 / CVE-2026-119375 is fixed by upstream SQLite 3.54.0.
    // This is a version-floor admission test, not an exploit reproduction.
    assert!(
        rusqlite::version_number() >= 3_054_000,
        "bundled SQLite {} predates the required upstream security fix",
        rusqlite::version()
    );
    let connection = rusqlite::Connection::open_in_memory().unwrap();
    let linked: String = connection
        .query_row("SELECT sqlite_version()", [], |r| r.get(0))
        .unwrap();
    assert_eq!(linked, rusqlite::version());
}
