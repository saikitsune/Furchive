#![cfg(feature = "fixture-media")]
use furchive_app::{
    account_session::AccountSession,
    credentials::{ProtectedStore, Vault},
};
use std::sync::Mutex;
#[derive(Default)]
struct Store(Mutex<Option<String>>);
impl ProtectedStore for Store {
    fn write(&self, s: &str) -> Result<(), String> {
        *self.0.lock().unwrap() = Some(s.into());
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
#[tokio::test]
async fn explicit_login_verifies_identity_before_protected_save_and_logout_removes_identity() {
    use std::io::{Read, Write};
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://{}/", listener.local_addr().unwrap());
    let server = std::thread::spawn(move || {
        for name in ["different_user", "fixture_user"] {
            let (mut s, _) = listener.accept().unwrap();
            let mut b = [0; 4096];
            let n = s.read(&mut b).unwrap();
            assert!(String::from_utf8_lossy(&b[..n]).starts_with("GET /users/me.json"));
            let body = format!("{{\"id\":123,\"name\":\"{name}\"}}");
            write!(
                s,
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                body.len()
            )
            .unwrap();
        }
    });
    let client = furchive_e621::E621Client::local_fixture(&origin).unwrap();
    let vault = Vault::with_store(Store::default());
    let session = AccountSession::new(client);
    assert!(
        session
            .login("fixture_user", "synthetic_test_key", &vault)
            .await
            .is_err()
    );
    assert!(vault.load().unwrap().is_none());
    assert!(session.status().await.is_none());
    assert_eq!(
        session
            .login("fixture_user", "synthetic_test_key", &vault)
            .await
            .unwrap()
            .name,
        "fixture_user"
    );
    assert_eq!(vault.load().unwrap().unwrap().username(), "fixture_user");
    assert!(session.logout(&Vault::with_store(Locked)).await.is_err());
    assert!(
        session.status().await.is_none(),
        "failed protected deletion must still revoke runtime identity"
    );
    session.logout(&vault).await.unwrap();
    assert!(session.status().await.is_none());
    assert!(vault.load().unwrap().is_none());
    server.join().unwrap();
}
#[tokio::test]
async fn logout_clears_runtime_even_when_protected_removal_fails() {
    let session = AccountSession::new(furchive_e621::E621Client::new().unwrap());
    session.deactivate().await;
    assert!(session.logout(&Vault::with_store(Locked)).await.is_err());
    assert!(session.status().await.is_none());
}
struct Locked;
impl ProtectedStore for Locked {
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
#[tokio::test]
async fn locked_vault_does_not_create_authenticated_session_or_plaintext_fallback() {
    let session = AccountSession::new(furchive_e621::E621Client::new().unwrap());
    let vault = Vault::with_store(Locked);
    assert!(session.restore(&vault).await.is_err());
    assert!(session.status().await.is_none());
}
