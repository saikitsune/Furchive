use furchive_app::{
    Database,
    browse::{Browser, PoolPage},
};
use furchive_domain::{PoolKey, RatingFilter, SearchQuery};
use furchive_e621::{E621Client, SearchPage, TagSuggestion};
use furchive_engine::storage::SavedSearch;
use tauri::{Manager, State};
pub struct AppState {
    pub browser: Browser<E621Client>,
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
    let api = E621Client::new().map_err(std::io::Error::other)?;
    #[cfg(feature = "self-test")]
    let api = if let Ok(base) = std::env::var("FURCHIVE_BROWSE_FIXTURE_ORIGIN") {
        if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_none() {
            return Err(std::io::Error::other("missing paired browser-test output").into());
        }
        E621Client::local_fixture(&base).map_err(std::io::Error::other)?
    } else {
        api
    };
    app.manage(AppState {
        browser: Browser::new(api, db.clone()),
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
    state.browser.search(&query, page).await
}
#[tauri::command]
pub async fn suggestions(
    state: State<'_, AppState>,
    prefix: String,
) -> Result<Vec<TagSuggestion>, String> {
    state.browser.suggestions(&prefix).await
}
#[tauri::command]
pub async fn favorites(
    state: State<'_, AppState>,
    query: SearchQuery,
    username: String,
    page: u32,
) -> Result<SearchPage, String> {
    state.browser.favorites(&query, &username, page).await
}
#[tauri::command]
pub async fn pool_page(
    state: State<'_, AppState>,
    id: u64,
    offset: usize,
    limit: usize,
    ratings: RatingFilter,
) -> Result<PoolPage, String> {
    state.browser.pool_page(id, offset, limit, &ratings).await
}
#[tauri::command]
pub async fn delete_search(state: State<'_, AppState>, id: String) -> Result<(), String> {
    state.db.delete_search(id).await
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
    state.browser.pins().await
}
#[tauri::command]
pub async fn set_pin(state: State<'_, AppState>, id: u64, pinned: bool) -> Result<(), String> {
    state.browser.set_pin(id, pinned).await
}
