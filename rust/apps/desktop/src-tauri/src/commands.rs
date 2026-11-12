use furchive_app::{
    Database,
    browse::{Browser, PoolPage},
    settings::{Preferences, SettingsStore},
};
use furchive_domain::{PoolKey, RatingFilter, SearchQuery};
use furchive_e621::{E621Client, SearchPage, TagSuggestion};
use furchive_engine::storage::SavedSearch;
use tauri::{Manager, State};
pub struct AppState {
    pub browser: Browser<E621Client>,
    pub db: Database,
    pub settings: SettingsStore,
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
        settings: SettingsStore::open(&root.join("settings.json"))
            .map_err(std::io::Error::other)?,
        db,
    });
    Ok(())
}
fn state_cache(app: &tauri::AppHandle) -> std::sync::Arc<furchive_app::media_cache::MediaCache> {
    app.state::<std::sync::Arc<furchive_app::media_cache::MediaCache>>()
        .inner()
        .clone()
}
#[derive(serde::Serialize)]
pub struct PreparedMedia {
    id: String,
    kind: String,
    src: String,
    title: String,
}
#[tauri::command]
pub async fn prepare_media(
    app: tauri::AppHandle,
    id: String,
    variant: furchive_app::media_cache::Variant,
) -> Result<PreparedMedia, String> {
    let cache = state_cache(&app);
    let handle = cache.prepare(&id, variant).await?;
    let value = cache.get(&handle).ok_or("media was evicted")?;
    let server = app.state::<crate::media_loopback::MediaServer>();
    Ok(PreparedMedia {
        id: id.clone(),
        kind: if value.mime.starts_with("video/") {
            "video".into()
        } else {
            "image".into()
        },
        src: format!("{}media/{handle}", server.base_url()),
        title: format!("Post {id}"),
    })
}
#[tauri::command]
pub fn preferences(state: State<'_, AppState>) -> Result<Preferences, String> {
    Ok(state.settings.current()?.into())
}
#[tauri::command]
pub async fn save_preferences(
    state: State<'_, AppState>,
    preferences: Preferences,
) -> Result<Preferences, String> {
    state.settings.apply(preferences).map(Into::into)
}
#[tauri::command]
pub async fn search(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    query: SearchQuery,
    page: u32,
) -> Result<SearchPage, String> {
    let cache = state_cache(&app);
    let generation = cache.begin_page()?;
    let result = state.browser.search(&query, page).await?;
    cache.authorize_page(generation, &result.items)?;
    Ok(result)
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
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    query: SearchQuery,
    username: String,
    page: u32,
) -> Result<SearchPage, String> {
    let cache = state_cache(&app);
    let generation = cache.begin_page()?;
    let result = state.browser.favorites(&query, &username, page).await?;
    cache.authorize_page(generation, &result.items)?;
    Ok(result)
}
#[tauri::command]
pub async fn pool_page(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    id: u64,
    offset: usize,
    limit: usize,
    ratings: RatingFilter,
) -> Result<PoolPage, String> {
    let cache = state_cache(&app);
    let generation = cache.begin_page()?;
    let result = state.browser.pool_page(id, offset, limit, &ratings).await?;
    cache.authorize_page(
        generation,
        &result
            .entries
            .iter()
            .filter_map(|e| e.post.clone())
            .collect::<Vec<_>>(),
    )?;
    Ok(result)
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
