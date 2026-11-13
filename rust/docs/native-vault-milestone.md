# Real OS protected-store milestone

Exact source `1e9b965f840408b39f8da0e1cbd7e322067ba93d`, run https://github.com/saikitsune/Furchive/actions/runs/43332837342 : all **nine** jobs completed SUCCESS. Owner downloaded all three vault ZIPs, verified GitHub artifact SHA256 and independently parsed exact namespace/platform/phase receipts. Four distinct native processes wrote synthetic credentials, verified exact username and key, deleted them and proved absence.

| Real backend | Artifact ZIP SHA256 |
|---|---|
| Linux Secret Service | `11ccbab7a7ee50dd073ef32199730d28185bfc224e934fab82b65b63cabe3532` |
| Windows Credential Manager | `1911d5ea026a11f67fb61f585b1fd86492a18bd3e287fb91d6e0c283ece7f0a1` |
| macOS Keychain | `d12f0a3d0ae11f3cde021650f747faafc0f1d744f4405cab8dc936c0d83ff40e` |

Linux used a private session bus, HOME/XDG/control directories and ephemeral keyring, with owned daemon cleanup. Windows/macOS used unique qualification-only entries on disposable hosted runners. Production active-account entry cannot be selected by this probe; no real user credentials were read, requested, copied or logged. All synthetic entries were absent after deletion. The same native store factory as production was exercised. This closes basic native persistence/read/delete proof, not locked-store/user-prompt behavior or the complete account UI combined with a real native store.

Owner also downloaded actual three native application ZIPs and verified package/executable hashes,20+6 UI/restart contracts, actual Range/If-Range,108 raw PNGs and media runtime receipts. Evidence under `rust/test-artifacts/ci-run-43332837342/{owner-vault-verification.json,owner-native-verification.json}`. Native packages are engineering qualification artifacts, not release approval.

Prior Windows WebView2 shutdown Error1412 and Linux accessibility/GPU warnings remain documented unresolved concerns. Do not suppress them or claim clean shutdown/GPU/accessibility acceptance. Broader platform/architecture/Wayland/HiDPI/audio/memory/clean-install/final review and feature parity remain open. Overall application **NOT COMPLETE**; main and C# preserved.
