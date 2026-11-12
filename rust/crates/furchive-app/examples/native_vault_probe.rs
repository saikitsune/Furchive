//! Native-only, isolated test entry. Never accepts active-account or prints secrets.
use furchive_app::credentials::Vault;
fn main() {
    if let Err(error) = run() {
        eprintln!("NATIVE VAULT QUALIFICATION FAILED: {error}");
        std::process::exit(1);
    }
}
fn run() -> Result<(), String> {
    let args = std::env::args().collect::<Vec<_>>();
    if args.len() != 3 {
        return Err("expected phase and qualification namespace".into());
    }
    let phase = &args[1];
    let scope = &args[2];
    let vault = Vault::native_qualification(scope)?;
    let mut wrote = false;
    let result: Result<(), String> = (|| {
        match phase.as_str() {
            "write" => {
                if vault.load()?.is_some() {
                    return Err("test namespace already exists; refusing replacement".into());
                }
                vault.save("fixture_user", "synthetic-native-vault-qualification-only")?;
                wrote = true;
            }
            "read" => {
                if !vault.qualification_matches(
                    "fixture_user",
                    "synthetic-native-vault-qualification-only",
                )? {
                    return Err("native entry identity mismatch".into());
                }
            }
            "delete" => {
                vault.delete()?;
                if vault.load()?.is_some() {
                    return Err("native entry removal not acknowledged".into());
                }
            }
            "absent" => {
                if vault.load()?.is_some() {
                    return Err("native entry survived deletion".into());
                }
            }
            _ => return Err("unknown native vault test phase".into()),
        }
        Ok(())
    })();
    if result.is_err() && wrote {
        let _ = vault.delete();
    }
    result?;
    println!(
        "{}",
        serde_json::json!({"schema":1,"phase":phase,"namespace":scope,"native_store":std::env::consts::OS,"ok":true,"synthetic_data":true})
    );
    Ok(())
}
