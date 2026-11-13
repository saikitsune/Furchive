#![cfg(feature = "native-vault-test")]
#[cfg(feature = "fixture-media")]
#[test]
fn qualification_matches_compares_both_fields_without_exposing_secret() {
    let vault = furchive_app::credentials::Vault::fixture();
    vault.save("fixture_user", "synthetic_only").unwrap();
    assert!(
        vault
            .qualification_matches("fixture_user", "synthetic_only")
            .unwrap()
    );
    assert!(
        !vault
            .qualification_matches("fixture_user", "different")
            .unwrap()
    );
    assert!(
        !vault
            .qualification_matches("different", "synthetic_only")
            .unwrap()
    );
    vault.delete().unwrap();
    assert!(
        !vault
            .qualification_matches("fixture_user", "synthetic_only")
            .unwrap()
    );
}
#[test]
fn qualification_entry_namespace_refuses_active_or_unbounded_accounts_before_native_store() {
    for scope in [
        "active-account",
        "",
        "../../other",
        "qualification-",
        "qualification-123",
        &format!("qualification-{}", "a".repeat(65)),
    ] {
        assert!(
            furchive_app::credentials::Vault::native_qualification(scope).is_err(),
            "{scope}"
        );
    }
}
