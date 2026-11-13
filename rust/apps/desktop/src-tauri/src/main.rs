#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
#[cfg(feature = "self-test")]
mod account_probe;
mod commands;
mod media_geometry;
mod media_loopback;
mod media_protocol;
mod media_test;
#[tauri::command]
fn browse_test_phase() -> u8 {
    #[cfg(feature = "self-test")]
    if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_some() {
        return std::env::var("FURCHIVE_BROWSE_TEST_PHASE")
            .ok()
            .and_then(|s| s.parse().ok())
            .filter(|n| matches!(n, 1..=4))
            .unwrap_or(0);
    }
    0
}
#[tauri::command]
fn qualification_mode() -> bool {
    cfg!(feature = "self-test") && browse_test_phase() == 0
}
#[tauri::command]
fn qualification_result(app: tauri::AppHandle, result: serde_json::Value) -> Result<(), String> {
    #[cfg(feature = "self-test")]
    {
        let mut result = result;
        if std::env::var_os("FURCHIVE_BROWSE_TEST_OUTPUT").is_some() {
            use tauri::Manager;
            let state = app.state::<commands::AppState>();
            let expected = state.settings.current()?.resident_media_budget();
            let cache = app.state::<std::sync::Arc<furchive_app::media_cache::MediaCache>>();
            if cache.budget_bytes()? != expected || cache.resident_bytes()? > expected {
                return Err("actual cache budget not applied".into());
            }
        }
        if matches!(browse_test_phase(), 3 | 4) && result["ok"] == true {
            let scope = commands::account_ui_namespace()?
                .ok_or("missing isolated native store namespace")?;
            let vault = furchive_app::credentials::Vault::native_qualification(&scope)?;
            let expected = browse_test_phase() == 3;
            if vault.qualification_matches(
                "fixture_user",
                "synthetic-native-vault-qualification-only",
            )? != expected
            {
                return Err("actual native entry state differs from account UI".into());
            }
            result["credential_store"] = serde_json::json!("native");
            result["native_store"] = serde_json::json!(std::env::consts::OS);
            result["namespace"] = serde_json::json!(scope);
        }
        let path = std::env::var_os("FURCHIVE_QUALIFICATION_OUTPUT")
            .ok_or("missing qualification output")?;
        std::fs::write(
            path,
            serde_json::to_vec_pretty(&result).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        app.exit(0);
        Ok(())
    }
    #[cfg(not(feature = "self-test"))]
    {
        let _ = (app, result);
        Err("Qualification is not enabled in this build".into())
    }
}
#[tauri::command]
fn qualification_media_base(state: tauri::State<'_, media_loopback::MediaServer>) -> String {
    state.base_url()
}
fn main() {
    #[cfg(feature = "self-test")]
    if account_probe::requested() {
        if let Err(error) = account_probe::run() {
            eprintln!("NATIVE ACCOUNT PROBE FAILED: {error}");
            std::process::exit(1);
        }
        return;
    }
    // Permit only this launch's exact media origin, preserving the rest of the CSP.
    let cache =
        furchive_app::media_cache::MediaCache::new(128 * 1024 * 1024).expect("bounded media cache");
    #[cfg(feature = "self-test")]
    let cache = if let Ok(origin) = std::env::var("FURCHIVE_BROWSE_FIXTURE_ORIGIN") {
        furchive_app::media_cache::MediaCache::local_fixture(128 * 1024 * 1024, &origin)
            .expect("paired test media origin")
    } else {
        cache
    };
    let cache = std::sync::Arc::new(cache);
    let server =
        media_loopback::MediaServer::with_cache(cache.clone()).expect("scoped media transport");
    let origin = format!("http://{}", server.address());
    let mut context = tauri::generate_context!();
    let security = &mut context.config_mut().app.security;
    let policy = security.csp.take().expect("explicit bundled CSP");
    let mut directives: std::collections::HashMap<
        String,
        tauri::utils::config::CspDirectiveSources,
    > = policy.into();
    directives
        .get_mut("media-src")
        .expect("explicit media policy")
        .push(origin.clone());
    directives
        .get_mut("img-src")
        .expect("explicit image policy")
        .push(origin);
    security.csp = Some(tauri::utils::config::Csp::DirectiveMap(directives));
    tauri::Builder::default()
        .manage(cache)
        .manage(server)
        .setup(commands::initialize)
        .register_asynchronous_uri_scheme_protocol("fixture", |_ctx, request, responder| {
            let name = request.uri().path().trim_start_matches('/');
            let file: Option<(&[u8], &str)> = match name {
                "vp8.webm" => Some((
                    include_bytes!("../../public/fixtures/vp8.webm"),
                    "video/webm",
                )),
                "vp9.webm" => Some((
                    include_bytes!("../../public/fixtures/vp9.webm"),
                    "video/webm",
                )),
                "h264.mp4" => Some((
                    include_bytes!("../../public/fixtures/h264.mp4"),
                    "video/mp4",
                )),
                _ => None,
            };
            let response = if let Some((data, mime)) = file {
                match media_protocol::select_range(
                    request.headers().get("range").and_then(|h| h.to_str().ok()),
                    data.len(),
                ) {
                    Ok((status, start, end)) => {
                        let mut response = tauri::http::Response::builder()
                            .status(status)
                            .header("Content-Type", mime)
                            .header("Accept-Ranges", "bytes")
                            .header("Content-Length", end - start);
                        if status == 206 {
                            response = response.header(
                                "Content-Range",
                                format!("bytes {}-{}/{}", start, end - 1, data.len()),
                            );
                        }
                        response.body(data[start..end].to_vec()).unwrap()
                    }
                    Err(()) => tauri::http::Response::builder()
                        .status(416)
                        .header("Content-Range", format!("bytes */{}", data.len()))
                        .body(Vec::new())
                        .unwrap(),
                }
            } else {
                tauri::http::Response::builder()
                    .status(404)
                    .body(Vec::new())
                    .unwrap()
            };
            responder.respond(response);
        })
        .on_page_load(|_, _payload| {
            #[cfg(feature = "self-test")]
            eprintln!(
                "Qualification page {:?}: {}",
                _payload.event(),
                _payload.url()
            );
        })
        .invoke_handler(tauri::generate_handler![
            media_test::media_test_mode,
            media_test::media_test_checkpoint,
            media_test::media_test_result,
            qualification_mode,
            browse_test_phase,
            qualification_media_base,
            qualification_result,
            commands::account_status,
            commands::account_login,
            commands::account_restore,
            commands::account_logout,
            commands::choose_download_directory,
            commands::queue_pool_page,
            commands::queue_whole_pool,
            commands::download_group_control,
            commands::queue_download,
            commands::download_jobs,
            commands::download_control,
            commands::legacy_preview,
            commands::legacy_confirm,
            commands::prepare_media,
            commands::viewer_step,
            commands::preferences,
            commands::save_preferences,
            commands::search,
            commands::favorites,
            commands::pool_page,
            commands::delete_search,
            commands::suggestions,
            commands::saved_searches,
            commands::save_search,
            commands::pinned_pools,
            commands::set_pin
        ])
        .build(context)
        .expect("Furchive desktop runtime")
        .run(|app, event| {
            if matches!(event, tauri::RunEvent::Exit) {
                use tauri::Manager;
                let state = app.state::<commands::AppState>();
                tauri::async_runtime::block_on(async {
                    let _ = state.downloads.shutdown().await;
                    let _ = state.db.shutdown().await;
                });
            }
        });
}
