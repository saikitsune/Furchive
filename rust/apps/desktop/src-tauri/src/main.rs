#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod commands;
mod media_loopback;
mod media_protocol;
#[tauri::command]
fn qualification_mode() -> bool {
    cfg!(feature = "self-test")
}
#[tauri::command]
fn qualification_result(app: tauri::AppHandle, result: serde_json::Value) -> Result<(), String> {
    #[cfg(feature = "self-test")]
    {
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
    // Permit only this launch's exact media origin, preserving the rest of the CSP.
    let server = media_loopback::MediaServer::start().expect("fixture media transport");
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
        .push(origin);
    security.csp = Some(tauri::utils::config::Csp::DirectiveMap(directives));
    tauri::Builder::default()
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
            qualification_mode,
            qualification_media_base,
            qualification_result,
            commands::search,
            commands::suggestions,
            commands::saved_searches,
            commands::save_search,
            commands::pinned_pools,
            commands::set_pin
        ])
        .run(context)
        .expect("Furchive desktop runtime");
}
