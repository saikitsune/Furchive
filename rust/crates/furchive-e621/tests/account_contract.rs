#[test]
fn account_credentials_are_not_serializable_or_debug_visible() {
    let account =
        furchive_e621::Account::new("artist_user".into(), "private-value".into()).unwrap();
    let text = format!("{account:?}");
    assert!(!text.contains("private-value"));
    assert_eq!(account.username(), "artist_user");
}
#[test]
fn account_rejects_header_control_characters_and_missing_fields() {
    assert!(furchive_e621::Account::new("".into(), "key".into()).is_err());
    assert!(furchive_e621::Account::new("user".into(), "key\r\ninvalid".into()).is_err());
}
