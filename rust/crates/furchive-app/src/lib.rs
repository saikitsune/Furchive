pub mod browse;
pub mod credentials;
pub mod downloads;
pub mod legacy;
pub mod legacy_session;
pub mod media_cache;
pub mod settings;
use furchive_engine::storage::{CacheStore, SavedSearch, StateStore};
use std::path::Path;
use tokio::sync::{mpsc, oneshot};
type Reply<T> = oneshot::Sender<Result<T, String>>;
enum Command {
    Save(SavedSearch, Reply<()>),
    Searches(Reply<Vec<SavedSearch>>),
    DeleteSearch(String, Reply<()>),
    Posts(Vec<furchive_domain::MediaItem>, Reply<()>),
    Pin(furchive_domain::PoolKey, bool, Reply<()>),
    Pins(Reply<Vec<furchive_domain::PoolKey>>),
    Pool(furchive_domain::Pool, Reply<()>),
    Import(legacy::ConfirmedImport, Reply<legacy::ImportReport>),
    Stop(Reply<()>),
}
#[derive(Clone)]
pub struct Database {
    tx: mpsc::Sender<Command>,
}
impl Database {
    pub async fn open(root: &Path) -> Result<Self, String> {
        let root = root.to_owned();
        let (tx, mut rx) = mpsc::channel(32);
        let (ready, wait) = oneshot::channel();
        std::thread::Builder::new()
            .name("furchive-db".into())
            .spawn(move || {
                let result = (|| {
                    std::fs::create_dir_all(&root).map_err(|e| e.to_string())?;
                    Ok::<_, String>((
                        CacheStore::open(root.join("cache.sqlite")).map_err(|e| e.to_string())?,
                        StateStore::open(root.join("state.sqlite")).map_err(|e| e.to_string())?,
                    ))
                })();
                let (mut cache, mut state) = match result {
                    Ok(stores) => {
                        let _ = ready.send(Ok(()));
                        stores
                    }
                    Err(e) => {
                        let _ = ready.send(Err(e));
                        return;
                    }
                };
                while let Some(command) = rx.blocking_recv() {
                    match command {
                        Command::Save(s, r) => {
                            let _ = r.send(state.save_search(&s).map_err(|e| e.to_string()));
                        }
                        Command::Searches(r) => {
                            let _ = r.send(state.saved_searches().map_err(|e| e.to_string()));
                        }
                        Command::DeleteSearch(id, r) => {
                            let _ = r.send(state.delete_search(&id).map_err(|e| e.to_string()));
                        }
                        Command::Posts(items, r) => {
                            let _ = r.send(cache.upsert_posts(&items).map_err(|e| e.to_string()));
                        }
                        Command::Pin(key, pin, r) => {
                            let _ = r.send(state.set_pin(&key, pin).map_err(|e| e.to_string()));
                        }
                        Command::Pins(r) => {
                            let _ = r.send(state.pinned_pools().map_err(|e| e.to_string()));
                        }
                        Command::Pool(pool, r) => {
                            let _ = r.send(cache.reconcile_pool(&pool).map_err(|e| e.to_string()));
                        }
                        Command::Import(plan, r) => {
                            let result = (|| {
                                state.checkpoint().map_err(|e| e.to_string())?;
                                legacy::import_into(plan, &root.join("state.sqlite"))
                            })();
                            let _ = r.send(result);
                        }
                        Command::Stop(r) => {
                            drop(cache);
                            drop(state);
                            let _ = r.send(Ok(()));
                            break;
                        }
                    }
                }
            })
            .map_err(|e| e.to_string())?;
        wait.await.map_err(|_| "DB worker stopped")??;
        Ok(Self { tx })
    }
    async fn request<T>(&self, build: impl FnOnce(Reply<T>) -> Command) -> Result<T, String> {
        let (tx, rx) = oneshot::channel();
        self.tx
            .send(build(tx))
            .await
            .map_err(|_| "DB worker closed")?;
        rx.await.map_err(|_| "DB reply lost")?
    }
    pub async fn save_search(&self, s: SavedSearch) -> Result<(), String> {
        self.request(|r| Command::Save(s, r)).await
    }
    pub async fn delete_search(&self, id: String) -> Result<(), String> {
        self.request(|r| Command::DeleteSearch(id, r)).await
    }
    pub async fn searches(&self) -> Result<Vec<SavedSearch>, String> {
        self.request(Command::Searches).await
    }
    pub async fn store_posts(&self, p: Vec<furchive_domain::MediaItem>) -> Result<(), String> {
        self.request(|r| Command::Posts(p, r)).await
    }
    pub async fn set_pin(&self, k: furchive_domain::PoolKey, pin: bool) -> Result<(), String> {
        self.request(|r| Command::Pin(k, pin, r)).await
    }
    pub async fn pins(&self) -> Result<Vec<furchive_domain::PoolKey>, String> {
        self.request(Command::Pins).await
    }
    pub async fn store_pool(&self, p: furchive_domain::Pool) -> Result<(), String> {
        self.request(|r| Command::Pool(p, r)).await
    }
    pub async fn import_legacy(
        &self,
        plan: legacy::ConfirmedImport,
    ) -> Result<legacy::ImportReport, String> {
        self.request(|r| Command::Import(plan, r)).await
    }
    pub async fn shutdown(&self) -> Result<(), String> {
        self.request(Command::Stop).await
    }
}

#[cfg(test)]
mod tests {
    #[tokio::test]
    async fn saved_search_delete_is_acknowledged_and_survives_restart() {
        let dir = tempfile::tempdir().unwrap();
        let db = super::Database::open(dir.path()).await.unwrap();
        db.save_search(furchive_engine::storage::SavedSearch {
            id: "fixture".into(),
            name: "Fixture".into(),
            query: Default::default(),
        })
        .await
        .unwrap();
        assert_eq!(db.searches().await.unwrap().len(), 1);
        db.delete_search("fixture".into()).await.unwrap();
        assert!(db.searches().await.unwrap().is_empty());
        db.shutdown().await.unwrap();
        let db = super::Database::open(dir.path()).await.unwrap();
        assert!(db.searches().await.unwrap().is_empty());
        db.shutdown().await.unwrap();
    }
    #[tokio::test]
    async fn db_actor_keeps_user_state_and_closes_before_reopen() {
        use furchive_engine::storage::SavedSearch;
        let dir = tempfile::tempdir().unwrap();
        let db = super::Database::open(dir.path()).await.unwrap();
        db.save_search(SavedSearch {
            id: "one".into(),
            name: "Foxes".into(),
            query: Default::default(),
        })
        .await
        .unwrap();
        assert_eq!(db.searches().await.unwrap().len(), 1);
        db.shutdown().await.unwrap();
        let db = super::Database::open(dir.path()).await.unwrap();
        assert_eq!(db.searches().await.unwrap()[0].name, "Foxes");
        db.shutdown().await.unwrap();
    }
}
