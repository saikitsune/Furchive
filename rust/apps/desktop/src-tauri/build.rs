fn main() {
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "media_test_mode",
            "media_test_checkpoint",
            "media_test_result",
            "qualification_mode",
            "qualification_media_base",
            "qualification_result",
            "browsing_session",
            "forget_browsing_session",
            "clear_cache",
            "preview_templates",
            "search",
            "suggestions",
            "saved_searches",
            "save_search",
            "pinned_pools",
            "set_pin",
        ]),
    ))
    .expect("Tauri build configuration");
}
