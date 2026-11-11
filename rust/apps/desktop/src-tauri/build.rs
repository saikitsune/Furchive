fn main() {
    tauri_build::try_build(tauri_build::Attributes::new().app_manifest(
        tauri_build::AppManifest::new().commands(&[
            "qualification_mode",
            "qualification_result",
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
