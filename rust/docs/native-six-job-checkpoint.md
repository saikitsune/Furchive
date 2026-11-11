# Whole native qualification run — six jobs verified

Exact candidate `7deb877701bdc0f13c7cf3567648c4858017df98`
Run https://github.com/saikitsune/Furchive/actions/runs/43114513874 completed SUCCESS.
All six actual jobs read back: three download-contract jobs and three desktop/package jobs. This is a tested engineering checkpoint, not a completed Furchive application or release.

## Owner verification

Download artifacts and their exact named contracts were already verified in `portable-downloads-native-milestone.md`: Linux44/macOS43/Windows45 unit tests, each1doctest/fmt/strict Clippy, source/inventory/log checks. No new count inferred from the global status.

Owner separately downloaded all three `native-qualification-*` artifacts for THIS run, verified GitHub ZIP digests, package SHA256, and executable bytes read directly from the contained package against the native execution record. Independently ran the existing qualification receipt validator on both raw native-executable and extracted-package receipts for each OS. All pass for five-image decode, three muted videos play/seek/progression,10,000-record/40-mounted-tile gallery.

| Platform | Artifact ID | Package SHA256 | Executed binary SHA256 |
|---|---|---|---|
| Linux |13897319449|`c49efa7ec0afbb0c4d3a4a66949d25eded2c29f893413d76b8e55719f3fe95ac`|`12197a477a676b11c2f6e56ef969f6733bd58c8e0e8584b2fa83a2504eb21212`|
| macOS |13898132366|`aace9a2402dab9ced1794d934d3a832658552572b63199b12e525c130be7224a`|`a0e711dae2d200b2cac35091df711f85d7b8728951f45525a0dfddabc05eea04`|
| Windows |13897843897|`cac6cf3717f0cd9ab43bedaa658f1c41bb167f1fcedb1c514a8c6cb40bf6e1db`|`dfa17d19c3d536a981a5c45c8fe7b6bac02274802140aae034ee2508cda0476b`|

Raw local evidence: `rust/test-artifacts/ci-run-43114513874/whole-run-package-verification.json` plus platform payload/receipt/log directories; download verification separate in same root. Preserve native GPU/audio/accessibility/shutdown diagnostics and the original outage/failing Windows fixture history. Basic decode/playback/virtualized-DOM pass does not establish audible output, animated frame progression, FPS/decoded-memory budgets, full lifecycle, Wayland or every CPU architecture. Packages are self-test debug engineering artifacts, explicitly `release_ready:false`; no install/signing/notarization/full-app claim.

## Active continuation, not idle after green CI

Legacy import worker `t_f0939a14` is running, dependent verifier `t_d18d39a6` waits for its freeze; scope limited to legacy module/new tests/docs. Live owner handled a prerequisite request immediately: added `rusqlite0.37` bundled/limits/hooks and `sha2 0.10` to app dependencies for transactional bounded SQLite snapshots and nonsecret source identity. Root and desktop lock reconciliation preserved all previous package versions/checksums/sources; root adds seven sha2 support packages and desktop adds no packages. Both `cargo tree --locked --offline` commands pass. This verifies dependency resolution only, not yet the in-flight legacy code. Dependencies/source are uncommitted together until that milestone is actually tested. No duplicate worker/review graph, no user-only choice required, no profile/model/provider/credential/safety changes.

Full product UI/Downloads integration, broader media acceptance, legacy migration UI and full application acceptance remain unfinished. Main and public releases are unchanged.
