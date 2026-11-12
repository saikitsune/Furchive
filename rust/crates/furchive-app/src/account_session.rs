//! Explicit protected credential restoration/verification. No implicit startup auth.
use crate::{browse::Provider, credentials::Vault};
use furchive_domain::{MediaItem, Pool, SearchQuery};
use furchive_e621::{Account, E621Client, SearchPage, TagSuggestion, VerifiedIdentity};
#[derive(Clone)]
pub struct AccountSession {
    inner: std::sync::Arc<tokio::sync::Mutex<Session>>,
}
struct Session {
    anonymous: E621Client,
    client: E621Client,
    identity: Option<VerifiedIdentity>,
}
impl AccountSession {
    pub fn new(anonymous: E621Client) -> Self {
        Self {
            inner: std::sync::Arc::new(tokio::sync::Mutex::new(Session {
                client: anonymous.clone(),
                anonymous,
                identity: None,
            })),
        }
    }
    pub async fn deactivate(&self) {
        let mut inner = self.inner.lock().await;
        inner.client = inner.anonymous.clone();
        inner.identity = None;
    }
    pub async fn status(&self) -> Option<VerifiedIdentity> {
        self.inner.lock().await.identity.clone()
    }
    pub async fn login(
        &self,
        username: &str,
        key: &str,
        vault: &Vault,
    ) -> Result<VerifiedIdentity, String> {
        let account = Account::new(username.into(), key.into())?;
        let mut inner = self.inner.lock().await;
        let client = inner.anonymous.clone().with_account(account);
        let identity = client.verify_identity().await?;
        vault.save(username, key)?;
        inner.client = client;
        inner.identity = Some(identity.clone());
        Ok(identity)
    }
    pub async fn restore(&self, vault: &Vault) -> Result<Option<VerifiedIdentity>, String> {
        let mut inner = self.inner.lock().await;
        let Some(account) = vault.load()? else {
            return Ok(None);
        };
        let client = inner.anonymous.clone().with_account(account);
        let identity = client.verify_identity().await?;
        inner.client = client;
        inner.identity = Some(identity.clone());
        Ok(Some(identity))
    }
    pub async fn logout(&self, vault: &Vault) -> Result<(), String> {
        let mut inner = self.inner.lock().await;
        inner.client = inner.anonymous.clone();
        inner.identity = None;
        // Failure still deauthenticates current runtime, and surfaces that vault removal
        // needs attention. Never silently restore old credentials on the next launch.
        vault.delete()
    }
}
impl Provider for AccountSession {
    async fn search(&self, q: &SearchQuery, page: u32, limit: usize) -> Result<SearchPage, String> {
        self.inner.lock().await.client.search(q, page, limit).await
    }
    async fn pool(&self, id: u64) -> Result<Pool, String> {
        self.inner.lock().await.client.pool(id).await
    }
    async fn posts(&self, ids: &[String]) -> Result<Vec<MediaItem>, String> {
        self.inner.lock().await.client.posts_by_ids(ids).await
    }
    async fn suggestions(&self, prefix: &str) -> Result<Vec<TagSuggestion>, String> {
        self.inner.lock().await.client.suggestions(prefix, 10).await
    }
}
