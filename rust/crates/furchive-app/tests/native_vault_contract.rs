#![cfg(feature = "native-vault-test")]
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
