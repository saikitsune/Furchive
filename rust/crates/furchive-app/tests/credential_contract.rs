use furchive_app::credentials::{ProtectedStore, Vault};
use std::sync::Mutex;
#[derive(Default)]
struct MemoryStore(Mutex<Option<String>>);
impl ProtectedStore for MemoryStore {
    fn write(&self, value: &str) -> Result<(), String> {
        *self.0.lock().unwrap() = Some(value.into());
        Ok(())
    }
    fn read(&self) -> Result<Option<String>, String> {
        Ok(self.0.lock().unwrap().clone())
    }
    fn delete(&self) -> Result<(), String> {
        *self.0.lock().unwrap() = None;
        Ok(())
    }
}
#[test]
fn vault_roundtrips_credentials_as_one_protected_entry() {
    let vault = Vault::with_store(MemoryStore::default());
    vault.save("fixture_user", "fixture_secret").unwrap();
    assert_eq!(vault.load().unwrap().unwrap().username(), "fixture_user");
    vault.delete().unwrap();
    assert!(vault.load().unwrap().is_none());
}
struct Unavailable;
impl ProtectedStore for Unavailable {
    fn write(&self, _: &str) -> Result<(), String> {
        Err("locked".into())
    }
    fn read(&self) -> Result<Option<String>, String> {
        Err("locked".into())
    }
    fn delete(&self) -> Result<(), String> {
        Err("locked".into())
    }
}
#[test]
fn unavailable_native_store_fails_instead_of_falling_back_to_json() {
    let vault = Vault::with_store(Unavailable);
    assert!(vault.save("fixture_user", "fixture_secret").is_err());
    assert!(vault.load().is_err());
}
