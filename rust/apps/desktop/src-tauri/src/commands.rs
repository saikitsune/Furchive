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
    pub browser: Browser<furchive_app::account_session::AccountSession>,
    pub account: furchive_app::account_session::AccountSession,
    #[cfg(feature = "self-test")]
    pub test_vault: furchive_app::credentials::Vault,
    pub db: Database,
    pub settings: SettingsStore,
    pub pool_snapshot: std::sync::Mutex<Option<PoolPage>>,
    pub downloads: furchive_app::downloads::Downloads,
    pub legacy: furchive_app::legacy_session::LegacySession,
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
    let downloads = furchive_app::downloads::Downloads::new(root.join("downloads.sqlite"));
    #[cfg(feature = "self-test")]
    let downloads = if let Ok(origin) = std::env::var("FURCHIVE_BROWSE_FIXTURE_ORIGIN") {
        furchive_app::downloads::Downloads::fixture(root.join("downloads.sqlite"), origin)
            .map_err(std::io::Error::other)?
    } else {
        downloads
    };
    let account = furchive_app::account_session::AccountSession::new(api);
    let settings =
        SettingsStore::open(&root.join("settings.json")).map_err(std::io::Error::other)?;
    app.state::<std::sync::Arc<furchive_app::media_cache::MediaCache>>()
        .set_budget(
            settings
                .current()
                .map_err(std::io::Error::other)?
                .resident_media_budget(),
        )
        .map_err(std::io::Error::other)?;
    app.manage(AppState {
        downloads,
        pool_snapshot: std::sync::Mutex::new(None),
        browser: Browser::new(account.clone(), db.clone()),
        account,
        #[cfg(feature = "self-test")]
        test_vault: furchive_app::credentials::Vault::fixture(),
        settings,
        legacy: furchive_app::legacy_session::LegacySession::new(),
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
    let state = app.state::<AppState>();
    let handle = cache
        .prepare_with_downloads(&id, variant, &state.downloads)
        .await?;
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
pub async fn legacy_preview(
    state: State<'_, AppState>,
) -> Result<Option<furchive_app::legacy_session::Preview>, String> {
    state.legacy.clear()?;
    #[cfg(feature = "self-test")]
    if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_some() {
        let root =
            std::env::var_os("FURCHIVE_TEST_DATA_ROOT").ok_or("missing isolated test profile")?;
        let path = std::path::PathBuf::from(root)
            .parent()
            .ok_or("missing isolated fixture parent")?
            .join("legacy-fixture");
        return state.legacy.select_directory(&path).map(Some);
    }
    let Some(folder) = rfd::AsyncFileDialog::new()
        .set_title("Select your old Furchive data directory (read-only preview)")
        .pick_folder()
        .await
    else {
        return Ok(None);
    };
    state.legacy.select_directory(folder.path()).map(Some)
}
#[tauri::command]
pub async fn legacy_confirm(
    state: State<'_, AppState>,
    digest: String,
    search_ids: Vec<String>,
    pool_ids: Vec<String>,
) -> Result<furchive_app::legacy::ImportReport, String> {
    let confirmed = state.legacy.confirmed(&digest, search_ids, pool_ids)?;
    state.db.import_legacy(confirmed).await
}
#[cfg(feature = "self-test")]
pub(crate) fn account_ui_namespace() -> Result<Option<String>, String> {
    let Some(scope) = std::env::var("FURCHIVE_ACCOUNT_UI_NAMESPACE").ok() else {
        return Ok(None);
    };
    if !matches!(crate::browse_test_phase(), 3 | 4)
        || std::env::var_os("FURCHIVE_TEST_DATA_ROOT").is_none()
        || std::env::var_os("FURCHIVE_BROWSE_FIXTURE_ORIGIN").is_none()
    {
        return Err("native account UI test requires paired isolated fixture".into());
    }
    validate_account_ui_scope(&scope)?;
    Ok(Some(scope))
}
#[cfg(feature = "self-test")]
fn validate_account_ui_scope(scope: &str) -> Result<(), String> {
    let suffix = scope
        .strip_prefix("qualification-")
        .ok_or("invalid native account UI namespace")?;
    if suffix.len() != 32
        || !suffix
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return Err("invalid native account UI namespace".into());
    }
    Ok(())
}
#[cfg(all(test, feature = "self-test"))]
mod account_scope_tests {
    #[test]
    fn rejects_production_or_malformed_qualification_names_before_native_entry() {
        assert!(
            super::validate_account_ui_scope(&format!("qualification-{}", "a".repeat(32))).is_ok()
        );
        for bad in [
            "active-account",
            "qualification-",
            "qualification-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "qualification-../active-account",
            "qualification-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaag",
        ] {
            assert!(super::validate_account_ui_scope(bad).is_err());
        }
    }
}
fn account_vault(state: &AppState) -> Result<furchive_app::credentials::Vault, String> {
    #[cfg(feature = "self-test")]
    if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_some() {
        if let Some(scope) = account_ui_namespace()? {
            return furchive_app::credentials::Vault::native_qualification(&scope);
        }
        return Ok(state.test_vault.clone());
    }
    let _ = state;
    furchive_app::credentials::Vault::native()
}
#[tauri::command]
pub async fn account_status(
    state: State<'_, AppState>,
) -> Result<Option<furchive_e621::VerifiedIdentity>, String> {
    Ok(state.account.status().await)
}
#[tauri::command]
pub async fn account_login(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    username: String,
    key: String,
) -> Result<furchive_e621::VerifiedIdentity, String> {
    state_cache(&app).begin_page()?;
    let vault = account_vault(&state)?;
    let result = state.account.login(&username, &key, &vault).await;
    state_cache(&app).begin_page()?;
    result
}
#[tauri::command]
pub async fn account_restore(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<Option<furchive_e621::VerifiedIdentity>, String> {
    state_cache(&app).begin_page()?;
    let vault = account_vault(&state)?;
    let result = state.account.restore(&vault).await;
    state_cache(&app).begin_page()?;
    result
}
#[tauri::command]
pub async fn account_logout(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    state_cache(&app).begin_page()?;
    state.account.deactivate().await;
    let vault = account_vault(&state)?;
    let result = state.account.logout(&vault).await;
    state_cache(&app).begin_page()?;
    result
}
#[tauri::command]
pub async fn choose_download_directory(state: State<'_, AppState>) -> Result<bool, String> {
    #[cfg(feature = "self-test")]
    if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_some() {
        let root = std::path::PathBuf::from(
            std::env::var_os("FURCHIVE_TEST_DATA_ROOT").ok_or("missing test root")?,
        );
        let root = root
            .parent()
            .ok_or("missing fixture parent")?
            .join("downloads-fixture");
        std::fs::create_dir_all(&root).map_err(|_| "cannot create test download root")?;
        state
            .downloads
            .select_root_with_concurrency(
                &root,
                state.settings.current()?.concurrent_downloads.into(),
            )
            .await?;
        return Ok(true);
    }
    let Some(folder) = rfd::AsyncFileDialog::new()
        .set_title("Choose Furchive download directory")
        .pick_folder()
        .await
    else {
        return Ok(false);
    };
    state
        .downloads
        .select_root_with_concurrency(
            folder.path(),
            state.settings.current()?.concurrent_downloads.into(),
        )
        .await?;
    Ok(true)
}
#[tauri::command]
pub async fn queue_download(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    id: String,
) -> Result<i64, String> {
    let post = state_cache(&app).authorized_post(&id)?;
    state
        .downloads
        .enqueue(&post, &state.settings.current()?)
        .await
}
#[tauri::command]
pub async fn queue_pool_page(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
) -> Result<String, String> {
    let page = state
        .pool_snapshot
        .lock()
        .map_err(|_| "pool snapshot poisoned")?
        .clone()
        .ok_or("open a pool page first")?;
    for post in page.entries.iter().filter_map(|e| e.post.as_ref()) {
        if state_cache(&app).authorized_post(post.key.id())? != *post {
            return Err("pool page authority changed".into());
        }
    }
    state
        .downloads
        .enqueue_pool_page(&page, &state.settings.current()?)
        .await
}
#[tauri::command]
pub async fn queue_whole_pool(
    state: State<'_, AppState>,
    id: u64,
    ratings: RatingFilter,
) -> Result<String, String> {
    state
        .account
        .queue_whole_pool(id, &ratings, &state.downloads, &state.settings.current()?)
        .await
}
#[tauri::command]
pub async fn download_group_control(
    state: State<'_, AppState>,
    id: String,
    verb: String,
) -> Result<(), String> {
    state.downloads.control_group(&id, &verb).await
}
#[tauri::command]
pub async fn download_jobs(
    state: State<'_, AppState>,
) -> Result<Vec<furchive_app::downloads::JobView>, String> {
    state.downloads.jobs().await
}
#[tauri::command]
pub async fn download_control(
    state: State<'_, AppState>,
    id: i64,
    verb: String,
) -> Result<(), String> {
    state.downloads.control(id, &verb).await
}
#[tauri::command]
pub fn preferences(state: State<'_, AppState>) -> Result<Preferences, String> {
    Ok(state.settings.current()?.into())
}
#[tauri::command]
pub async fn save_preferences(
    app: tauri::AppHandle,
    state: State<'_, AppState>,
    preferences: Preferences,
) -> Result<Preferences, String> {
    state
        .settings
        .apply_with_cache(preferences, &state_cache(&app))
        .map(Into::into)
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
    *state
        .pool_snapshot
        .lock()
        .map_err(|_| "pool snapshot poisoned")? = None;
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
    *state
        .pool_snapshot
        .lock()
        .map_err(|_| "pool snapshot poisoned")? = None;
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
    *state
        .pool_snapshot
        .lock()
        .map_err(|_| "pool snapshot poisoned")? = None;
    let result = state.browser.pool_page(id, offset, limit, &ratings).await?;
    cache.authorize_page(
        generation,
        &result
            .entries
            .iter()
            .filter_map(|e| e.post.clone())
            .collect::<Vec<_>>(),
    )?;
    *state
        .pool_snapshot
        .lock()
        .map_err(|_| "pool snapshot poisoned")? = Some(result.clone());
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
