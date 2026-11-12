//! Opt-in fixture-only native test IPC. No screenshot, caller path or arbitrary command API.
use serde::Deserialize;
#[cfg(feature = "self-test")]
use tauri::Manager;

#[tauri::command]
pub fn media_test_mode() -> bool {
    cfg!(feature = "self-test") && std::env::var_os("FURCHIVE_MEDIA_TEST_ROOT").is_some()
}

#[derive(Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
pub struct CaptureRequest {
    fixture: String,
    #[serde(rename = "fixtureSha256")]
    fixture_sha256: String,
    rect: Rect,
    viewport: Viewport,
    frames: u32,
    #[serde(rename = "intervalMs")]
    interval_ms: u32,
}
#[derive(Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
struct Rect {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}
#[derive(Deserialize, serde::Serialize)]
#[serde(deny_unknown_fields)]
struct Viewport {
    width: f64,
    height: f64,
    dpr: f64,
}
impl CaptureRequest {
    fn validate(&self, checkpoint: &str) -> Result<(), String> {
        if !matches!(checkpoint, "gif" | "apng" | "webp")
            || self.fixture != format!("viewer-animation.{checkpoint}")
        {
            return Err("unknown fixed media checkpoint".into());
        }
        let manifest: serde_json::Value =
            serde_json::from_str(include_str!("../../public/fixtures/viewer-manifest.json"))
                .map_err(|_| "invalid fixture manifest")?;
        let expected = manifest["files"]
            .as_array()
            .and_then(|rows| rows.iter().find(|r| r["name"] == self.fixture))
            .and_then(|r| r["sha256"].as_str())
            .ok_or("unknown fixture")?;
        if self.fixture_sha256 != expected || self.frames != 12 || self.interval_ms != 80 {
            return Err("fixture capture contract mismatch".into());
        }
        let r = &self.rect;
        let v = &self.viewport;
        if ![r.x, r.y, r.width, r.height, v.width, v.height, v.dpr]
            .iter()
            .all(|n| n.is_finite())
            || r.x < 0.0
            || r.y < 0.0
            || r.width <= 0.0
            || r.height <= 0.0
            || v.width <= 0.0
            || v.height <= 0.0
            || v.width > 4096.0
            || v.height > 4096.0
            || !(0.5..=4.0).contains(&v.dpr)
            || r.x + r.width > v.width + 1.0
            || r.y + r.height > v.height + 1.0
        {
            return Err("invalid capture geometry".into());
        }
        Ok(())
    }
}

#[tauri::command]
pub async fn media_test_checkpoint(
    app: tauri::AppHandle,
    checkpoint: String,
    receipt: CaptureRequest,
) -> Result<(), String> {
    if !media_test_mode() {
        return Err("media test IPC disabled".into());
    }
    receipt.validate(&checkpoint)?;
    #[cfg(feature = "self-test")]
    {
        let root = std::path::PathBuf::from(
            std::env::var_os("FURCHIVE_MEDIA_TEST_ROOT").ok_or("missing test root")?,
        );
        let window = app
            .get_webview_window("main")
            .ok_or("missing main test window")?;
        window
            .set_focus()
            .map_err(|_| "cannot focus owned test window")?;
        let position = window
            .inner_position()
            .map_err(|_| "cannot locate owned client")?;
        let window_size = window
            .inner_size()
            .map_err(|_| "cannot size owned window")?;
        // Capture the actual webview client, not the outer window's content view.
        // On macOS their bounds can differ (title/content-layout inset).
        let webview: &tauri::Webview = window.as_ref();
        #[cfg(not(target_os = "macos"))]
        let size = webview.size().map_err(|_| "cannot size owned webview")?;
        let bounds = webview
            .bounds()
            .map_err(|_| "cannot locate owned webview bounds")?;
        let scale = window
            .scale_factor()
            .map_err(|_| "cannot read owned scale")?;
        let offset = bounds.position.to_physical::<i32>(scale);
        let position = tauri::PhysicalPosition::new(position.x + offset.x, position.y + offset.y);
        #[cfg(target_os = "macos")]
        let (position, size) = macos_safe_client(&window, position, scale, &root)?;
        if (f64::from(size.width) - receipt.viewport.width * scale).abs() > 2.0
            || (f64::from(size.height) - receipt.viewport.height * scale).abs() > 2.0
            || (receipt.viewport.dpr - scale).abs() > 0.05
        {
            return Err(format!(
                "webview/native geometry mismatch: CSS={}x{} DPR={} native={}x{} window={}x{} scale={} offset={},{}",
                receipt.viewport.width,
                receipt.viewport.height,
                receipt.viewport.dpr,
                size.width,
                size.height,
                window_size.width,
                window_size.height,
                scale,
                offset.x,
                offset.y
            ));
        }
        let output = serde_json::json!({"pid":std::process::id(),"checkpoint":checkpoint,"request":receipt,
            "client":{"x":position.x,"y":position.y,"width":size.width,"height":size.height,"scale":scale}});
        let path = root.join(format!("{checkpoint}.request.json"));
        let temp = root.join(format!("{checkpoint}.request.tmp"));
        std::fs::write(
            &temp,
            serde_json::to_vec(&output).map_err(|_| "capture serialization")?,
        )
        .map_err(|_| "cannot write capture request")?;
        std::fs::rename(temp, path).map_err(|_| "cannot publish capture request")?;
        let ack = root.join(format!("{checkpoint}.ack"));
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(12);
        while std::time::Instant::now() < deadline {
            if std::fs::read(&ack).ok().as_deref() == Some(b"OK") {
                return Ok(());
            }
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        Err("host frame capture not acknowledged".into())
    }
    #[cfg(not(feature = "self-test"))]
    {
        let _ = (app, checkpoint, receipt);
        Err("media test IPC disabled".into())
    }
}
#[cfg(all(feature = "self-test", target_os = "macos"))]
fn macos_safe_client(
    window: &tauri::WebviewWindow,
    origin: tauri::PhysicalPosition<i32>,
    scale: f64,
    root: &std::path::Path,
) -> Result<(tauri::PhysicalPosition<i32>, tauri::PhysicalSize<u32>), String> {
    let (tx, rx) = std::sync::mpsc::sync_channel(1);
    window.with_webview(move|native|{
        // SAFETY: Tauri's pinned macOS PlatformWebview contains the WKWebView
        // instance (an NSView subclass), and invokes this closure on the main thread.
        let view: &objc2_app_kit::NSView=unsafe{&*native.inner().cast()};
        let bounds=view.bounds();let safe=view.safeAreaRect();
        let crop=crate::media_geometry::safe_crop([bounds.origin.x,bounds.origin.y,bounds.size.width,bounds.size.height],[safe.origin.x,safe.origin.y,safe.size.width,safe.size.height],view.isFlipped(),scale);
        let top=safe.origin.y-bounds.origin.y;let left=safe.origin.x-bounds.origin.x;
        let geometry=serde_json::json!({"bounds":{"x":bounds.origin.x,"y":bounds.origin.y,"width":bounds.size.width,"height":bounds.size.height},"safe":{"x":safe.origin.x,"y":safe.origin.y,"width":safe.size.width,"height":safe.size.height},"flipped":view.isFlipped(),"top":top,"left":left,"scale":scale});
        let value=crop.map(|c|(tauri::PhysicalPosition::new(origin.x+c[0].round() as i32,origin.y+c[1].round() as i32),tauri::PhysicalSize::new(c[2].round() as u32,c[3].round() as u32),geometry));
        let _=tx.send(value);
    }).map_err(|_|"cannot read native AppKit geometry")?;
    let (position, size, geometry) = rx
        .recv_timeout(std::time::Duration::from_secs(3))
        .map_err(|_| "AppKit geometry query timed out")??;
    std::fs::write(
        root.join("native-geometry.json"),
        serde_json::to_vec_pretty(&geometry).map_err(|_| "cannot encode native geometry")?,
    )
    .map_err(|_| "cannot retain native geometry")?;
    Ok((position, size))
}

#[tauri::command]
pub fn media_test_result(app: tauri::AppHandle, result: serde_json::Value) -> Result<(), String> {
    if !media_test_mode() {
        return Err("media test IPC disabled".into());
    }
    #[cfg(feature = "self-test")]
    {
        let bytes = serde_json::to_vec_pretty(&result).map_err(|_| "cannot encode result")?;
        if bytes.len() > 128 * 1024 {
            return Err("media result exceeds128KiB".into());
        }
        let root = std::path::PathBuf::from(
            std::env::var_os("FURCHIVE_MEDIA_TEST_ROOT").ok_or("missing test root")?,
        );
        std::fs::write(root.join("runtime.json"), bytes)
            .map_err(|_| "cannot persist runtime result")?;
        app.exit(if result["ok"] == true { 0 } else { 1 });
        Ok(())
    }
    #[cfg(not(feature = "self-test"))]
    {
        let _ = (app, result);
        Err("media test IPC disabled".into())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn checkpoint_refuses_unknown_fixture_hash_and_outside_geometry() {
        let manifest: serde_json::Value =
            serde_json::from_str(include_str!("../../public/fixtures/viewer-manifest.json"))
                .unwrap();
        let mut r = CaptureRequest {
            fixture: "viewer-animation.gif".into(),
            fixture_sha256: manifest["files"][0]["sha256"].as_str().unwrap().into(),
            rect: Rect {
                x: 0.,
                y: 0.,
                width: 320.,
                height: 200.,
            },
            viewport: Viewport {
                width: 800.,
                height: 600.,
                dpr: 1.,
            },
            frames: 12,
            interval_ms: 80,
        };
        assert!(r.validate("gif").is_ok());
        assert!(r.validate("../../other").is_err());
        r.rect.x = 900.;
        assert!(r.validate("gif").is_err());
        r.rect.x = 0.;
        r.fixture_sha256 = "0".repeat(64);
        assert!(r.validate("gif").is_err());
    }
}
