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
    #[cfg(feature = "fixture-media")]
    pub fn fixture() -> Self {
        #[derive(Default)]
        struct Memory(Mutex<Option<String>>);
        impl ProtectedStore for Memory {
            fn write(&self, v: &str) -> Result<(), String> {
                *self.0.lock().map_err(|_| "test store lock")? = Some(v.into());
                Ok(())
            }
            fn read(&self) -> Result<Option<String>, String> {
                Ok(self.0.lock().map_err(|_| "test store lock")?.clone())
            }
            fn delete(&self) -> Result<(), String> {
                *self.0.lock().map_err(|_| "test store lock")? = None;
                Ok(())
            }
        }
        Self::with_store(Memory::default())
    }
    #[cfg(feature = "native-vault-test")]
    pub fn qualification_matches(&self, user: &str, key: &str) -> Result<bool, String> {
        let Some(payload) = self.store.read()? else {
            return Ok(false);
        };
        let actual: (String, String) =
            serde_json::from_str(&payload).map_err(|_| "invalid qualification entry")?;
        Ok(actual.0 == user && actual.1 == key)
    }
    pub fn native() -> Result<Self, String> {
        Self::native_entry("active-account")
    }
    #[cfg(feature = "native-vault-test")]
    pub fn native_qualification(scope: &str) -> Result<Self, String> {
        let suffix = scope
            .strip_prefix("qualification-")
            .ok_or("invalid native qualification namespace")?;
        if suffix.len() != 32
            || !suffix
                .bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
        {
            return Err("invalid native qualification namespace".into());
        }
        Self::native_entry(scope)
    }
    fn native_entry(account: &str) -> Result<Self, String> {
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
                .build("dev.furchive.desktop.alpha.e621", account, None)
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
