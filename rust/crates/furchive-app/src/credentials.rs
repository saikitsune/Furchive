//! Explicit native credential providers; no plaintext fallback or global default-store mutation.
use keyring_core::{Entry, api::CredentialStoreApi};
use std::sync::{Arc, Mutex};
pub trait ProtectedStore: Send + Sync {
    fn write(&self, value: &str) -> Result<(), String>;
    fn read(&self) -> Result<Option<String>, String>;
    fn delete(&self) -> Result<(), String>;
}
struct NativeStore {
    entry: Mutex<Entry>,
}
impl ProtectedStore for NativeStore {
    fn write(&self, value: &str) -> Result<(), String> {
        self.entry
            .lock()
            .map_err(|_| "credential worker poisoned")?
            .set_password(value)
            .map_err(|_| "native credential store is locked or unavailable".into())
    }
    fn read(&self) -> Result<Option<String>, String> {
        match self
            .entry
            .lock()
            .map_err(|_| "credential worker poisoned")?
            .get_password()
        {
            Ok(value) => Ok(Some(value)),
            Err(keyring_core::Error::NoEntry) => Ok(None),
            Err(_) => Err("native credential store is locked or unavailable".into()),
        }
    }
    fn delete(&self) -> Result<(), String> {
        match self
            .entry
            .lock()
            .map_err(|_| "credential worker poisoned")?
            .delete_credential()
        {
            Ok(()) | Err(keyring_core::Error::NoEntry) => Ok(()),
            Err(_) => Err("native credential removal failed".into()),
        }
    }
}
#[derive(Clone)]
pub struct Vault {
    store: Arc<dyn ProtectedStore>,
}
impl Vault {
    pub fn with_store(store: impl ProtectedStore + 'static) -> Self {
        Self {
            store: Arc::new(store),
        }
    }
    pub fn native() -> Result<Self, String> {
        #[cfg(target_os = "linux")]
        let store = zbus_secret_service_keyring_store::Store::new();
        #[cfg(target_os = "windows")]
        let store = windows_native_keyring_store::Store::new();
        #[cfg(target_os = "macos")]
        let store = apple_native_keyring_store::keychain::Store::new();
        #[cfg(any(target_os = "linux", target_os = "windows", target_os = "macos"))]
        {
            let entry = store
                .map_err(|_| "native credential store unavailable")?
                .build("dev.furchive.desktop.alpha.e621", "active-account", None)
                .map_err(|_| "cannot open native credential entry")?;
            Ok(Self::with_store(NativeStore {
                entry: Mutex::new(entry),
            }))
        }
        #[cfg(not(any(target_os = "linux", target_os = "windows", target_os = "macos")))]
        {
            Err("unsupported native credential target".into())
        }
    }
    pub fn save(&self, user: &str, key: &str) -> Result<(), String> {
        furchive_e621::Account::new(user.into(), key.into())?;
        let payload =
            serde_json::to_string(&(user, key)).map_err(|_| "credential serialization failed")?;
        self.store.write(&payload)
    }
    pub fn load(&self) -> Result<Option<furchive_e621::Account>, String> {
        let Some(payload) = self.store.read()? else {
            return Ok(None);
        };
        let (user, key): (String, String) =
            serde_json::from_str(&payload).map_err(|_| "stored credential format invalid")?;
        Ok(Some(furchive_e621::Account::new(user, key)?))
    }
    pub fn delete(&self) -> Result<(), String> {
        self.store.delete()
    }
}
