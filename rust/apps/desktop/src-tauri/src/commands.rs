use furchive_app::Database;
use furchive_domain::{PoolKey, SearchQuery};
use furchive_e621::{E621Client, SearchPage, TagSuggestion};
use furchive_engine::storage::SavedSearch;
use tauri::{Manager, State};
pub struct AppState {
    pub api: E621Client,
    pub db: Database,
}
pub fn initialize(app: &mut tauri::App) -> Result<(), Box<dyn std::error::Error>> {
    let root = app.path().app_data_dir()?;
    #[cfg(feature = "self-test")]
    let root = std::env::var_os("FURCHIVE_TEST_DATA_ROOT")
        .map(std::path::PathBuf::from)
        .unwrap_or(root);
    let db =
        tauri::async_runtime::block_on(Database::open(&root)).map_err(std::io::Error::other)?;
    app.manage(AppState {
        api: E621Client::new().map_err(std::io::Error::other)?,
        db,
    });
    Ok(())
}
#[tauri::command]
pub async fn search(
    state: State<'_, AppState>,
    query: SearchQuery,
    page: u32,
) -> Result<SearchPage, String> {
    let result = state.api.search(&query, page, 50).await?;
    state.db.store_posts(result.items.clone()).await?;
    Ok(result)
}
#[tauri::command]
pub async fn suggestions(
    state: State<'_, AppState>,
    prefix: String,
) -> Result<Vec<TagSuggestion>, String> {
    state.api.suggestions(&prefix, 10).await
}
#[tauri::command]
pub async fn saved_searches(state: State<'_, AppState>) -> Result<Vec<SavedSearch>, String> {
    state.db.searches().await
}
#[tauri::command]
pub async fn save_search(state: State<'_, AppState>, search: SavedSearch) -> Result<(), String> {
    state.db.save_search(search).await
}
#[tauri::command]
pub async fn pinned_pools(state: State<'_, AppState>) -> Result<Vec<PoolKey>, String> {
    state.db.pins().await
}
#[tauri::command]
pub async fn set_pin(state: State<'_, AppState>, id: u64, pinned: bool) -> Result<(), String> {
    if id == 0 {
        return Err("invalid pool ID".into());
    }
    state
        .db
        .set_pin(
            PoolKey::new("e621", id.to_string()).map_err(|e| e.to_string())?,
            pinned,
        )
        .await
}
