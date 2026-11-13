//! Self-test-only separate process readback using the SAME packaged code identity.
//! Keychain ACLs are not broadened to trust a different helper executable.
pub fn requested() -> bool {
    std::env::args().nth(1).as_deref() == Some("--native-vault-probe")
}
fn parse(args: &[String]) -> Result<(&str, &str), String> {
    if args.len() != 4
        || args[1] != "--native-vault-probe"
        || !matches!(args[2].as_str(), "read" | "absent" | "delete")
    {
        return Err("invalid fixed account probe invocation".into());
    }
    let scope = args[3]
        .strip_prefix("qualification-")
        .ok_or("invalid isolated namespace")?;
    if scope.len() != 32
        || !scope
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    {
        return Err("invalid isolated namespace".into());
    }
    Ok((&args[2], &args[3]))
}
pub fn run() -> Result<(), String> {
    let args = std::env::args().collect::<Vec<_>>();
    let (phase, scope) = parse(&args)?;
    let vault = furchive_app::credentials::Vault::native_qualification(scope)?;
    match phase {
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
                return Err("native deletion not acknowledged".into());
            }
        }
        "absent" => {
            if vault.load()?.is_some() {
                return Err("native entry unexpectedly present".into());
            }
        }
        _ => return Err("invalid fixed probe phase".into()),
    }
    println!(
        "{}",
        serde_json::json!({"schema":1,"phase":phase,"namespace":scope,"native_store":std::env::consts::OS,"ok":true,"synthetic_data":true})
    );
    Ok(())
}
#[cfg(test)]
mod tests {
    #[test]
    fn probe_shape_rejects_any_production_entry_or_arbitrary_operation() {
        for phase in ["read", "absent", "delete"] {
            let a = vec![
                "owned".into(),
                "--native-vault-probe".into(),
                phase.into(),
                format!("qualification-{}", "b".repeat(32)),
            ];
            assert!(super::parse(&a).is_ok());
        }
        for (phase, scope) in [
            ("read", "active-account"),
            ("write", "qualification-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ("delete", "qualification-../active-account"),
            ("read", "qualification-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"),
        ] {
            let a = vec![
                "owned".into(),
                "--native-vault-probe".into(),
                phase.into(),
                scope.into(),
            ];
            assert!(super::parse(&a).is_err());
        }
    }
}
