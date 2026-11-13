# Default-package smoke: bounded implementation, not platform acceptance

## Status and scope

This lane packages the **ordinary default desktop product**, with only the
`custom-protocol` packaging feature, never `self-test`. Production Rust, frontend,
IPC, permissions, capabilities and security policy are unchanged. The existing
`rust-qualification.yml` engineering campaign is unchanged. No TEST commands,
fixture HTTP origins, injected JavaScript, CDP or browser surrogate are used.

The new manually dispatched workflow is an **unfinished qualification lane**:
Windows and macOS intentionally return nonzero UNRUN before application launch;
Linux returns nonzero UNRUN if unprivileged network isolation is denied. A static
package success must never be counted as a default-runtime success. No CI push or
dispatch was performed while implementing this lane.

All payloads are explicitly **debug/unoptimized**, unsigned engineering artifacts:

- Linux: actual Tauri Debian, safely enumerated and extracted, not installed.
- macOS: complete `.app` archived/re-extracted, not a copied executable. Only the
  actual runner architecture is covered; macos-15 is not x64/universal approval.
- Windows: portable executable ZIP requiring system WebView2, **not NSIS/MSI**.

Each package receipt contains raw artifact/executable SHA-256 and complete file
payload names/sizes/hashes. Archive paths, aliases, duplicates, links and special
members are rejected. Fresh output directories are mandatory. SHA-256 alone is
not runtime approval, provenance, reproducible-build, dependency closure or legal
approval.

## Commands

From repository root, with canonical Rust **1.99.1**, Node **24** and native SDK:

```sh
python rust/scripts/default-test.py -v
npm ci --prefix rust/apps/desktop
# Linux, from rust/apps/desktop:
npm run tauri -- build --debug --features custom-protocol --bundles deb --ci -- --locked --message-format=json
# macOS, same directory:
npm run tauri -- build --debug --features custom-protocol --bundles app --ci --no-sign -- --locked --message-format=json
# Windows, repository root, after npm run build:
cargo +1.99.1 build --manifest-path rust/apps/desktop/src-tauri/Cargo.toml --features custom-protocol --locked --message-format=json
```

Retain combined raw build output as `build.log` (use pipefail when teeing). The
packager requires a unique actual Cargo compiler-artifact for the exact target
path with features **exactly `["custom-protocol"]`**. Tauri debug packaging does
**not** automatically enable that feature in this manifest: an initial featureless
local build was correctly rejected; its original build/refusal evidence remains.

Tauri cosmetically inserts spaces in the historical self-test feature array even
when it is not selected. `--restore-cosmetic-manifest` restores only that exact
known byte transformation from HEAD, refusing semantic or other-line drift. A
subsequent Git diff gate requires all product files to remain unchanged.

```sh
python rust/scripts/default-smoke.py --restore-cosmetic-manifest
python rust/scripts/default-smoke.py --prepare-only --build-log build.log --output rust/test-artifacts/default-package-Linux
# Only inside a dedicated Xvfb + Openbox + private dbus-run-session:
/usr/bin/python3 rust/scripts/default-smoke.py --runtime-package rust/test-artifacts/default-package-Linux/package.json --output rust/test-artifacts/default-runtime-Linux
```

See the workflow for exact pinned Actions, native dependencies, time budget,
artifact allowlist and five-day retention. `sudo apt-get` there installs hosted
builder prerequisites only; application execution never uses host root or a
sandbox bypass. The workflow has only contents:read and no automatic push trigger.

## Linux native protocol (implemented, hosted execution still UNRUN)

1. Validate immutable package/binary hashes and refuse stale evidence. Create a
   fresh unprivileged user+network namespace, verify a distinct network namespace
   and **only lo**, then enable only its private loopback. Namespace refusal is
   terminal: no sudo retry, disabling WebKit sandbox, alternative browser or
   unconstrained launch. This prevents external traffic; it does **not** prove no
   attempted external request. There is no request server/proxy or e621 authority.
2. Construct an allowlisted environment: task-owned HOME, XDG_DATA_HOME,
   XDG_CONFIG_HOME, XDG_CACHE_HOME, XDG_RUNTIME_DIR and TMPDIR; omit FURCHIVE_TEST_*,
   fixture flags, proxy credentials and sandbox-disable variables. Never point at
   real runner application data. The pinned Tauri 2.12.5 app_data_dir delegates to
   dirs 7.0.0 data_dir: Linux uses absolute XDG_DATA_HOME plus bundle identifier.
3. Seed only a harmless, random controls-only Pool ID snapshot in that owned
   settings file. No account, artwork, credential-store call, download grant,
   imported data or automatic search is supplied. Start the extracted executable
   directly with no test arguments.
4. Bind executable path, process birth time, PID and sole X11 client window. Refuse
   any peer client, ambiguous or missing window. Enumerate AT-SPI only for the
   launched process and verified descendants. Require the normal product region
   and persisted Pool ID text, not title-only/file-exists or a blank screenshot.
5. Invoke **normal product** Load preferences / Save preferences via the owned
   native accessibility tree; require the native Preferences saved acknowledgement
   and saved settings sentinel. Read actual open `state.sqlite` / `cache.sqlite`
   descriptors to establish the real data root. Capture only the exact owned X11
   drawable (XGetImage), not the desktop; reject blank/undersized frames.
6. Revalidate PID/path/window, send `_NET_CLOSE_WINDOW` via wmctrl (not xdotool
   windowclose/XDestroyWindow, kill, Alt-F4 or foreign-window focus). Require actual
   zero normal exit. Repeat a second process against the same data and require
   real product/settings read and save again. Cleanup never substitutes for close.
7. Retain raw native stdout/stderr, exit JSON, accessibility rows, screenshots and
   semantic receipt. Scoped session+birth-time cleanup reaps owned descendants;
   survivors fail. Chromium1412/Linux accessibility/ALSA/EGL/etc. diagnostics are
   never filtered away or called clean/audio/GPU acceptance.

The accessibility names, WebKit subtree ownership, pixel format, native dialogs,
and nested WebKit sandbox behavior still need an actual hosted campaign. A fake
adapter passing does not validate these OS integration assumptions. The harness
fails closed on unavailable UI/root observations. The screenshot nonblank check
is supplemental to semantics, never standalone product proof.

Outer supervision now requires a dedicated single-threaded Linux owner with no existing child processes and an initially unset kernel subreaper flag. Before launch, PR_SET_CHILD_SUBREAPER makes kernel-reparented orphans remain this owner's children across setsid/double-fork and parent exit. Ownership checks fail before launch on unsupported/concurrent owners. Cleanup observes adopted and direct descendants, terminates/kills only PID/birth-bound owned children, reaps adopted children separately and lets Popen alone reap its direct child. Subreaper state restores only after the owned tree is gone. A finite timeout stays non-success even when cleanup exits; raw SIGTERM remains -15, not fabricated zero.

Original b2b869bf/d4d9f4f5 remains BLOCKED: MAJOR NYX-DP-01 lost-parent survivors and MINOR NYX-DP-02 competing-reaper raw exit. Original source/evidence/repro retained. New actual sleeping/forking host fixtures cover zero/crash/lost-middle-timeout and raw SIGTERM, not native GUI. Original new regression3/3FAIL before correction; synchronized controls retain original negative/nonreproduction counts separately. Corrected normal/-O/-OO4/4PASS. The24named original host tests remain and their non-Linux supervisor path asserts prelaunch refusal rather than claiming a Windows subreaper. The new supervision script runs in the Linux workflow only.

D-Bus must use a filesystem Unix socket: abstract/TCP session addresses are refused inside the new network namespace. Actual isolated AT-SPI bus propagation remains a hosted evidence gap; inherited environment alone is not proof of accessibility availability. Status-label semantics, side effects, screenshots, roots and ordinary close still require actual native observation; this repair does not certify them.

## Honest platform gaps

**Windows:** pinned dirs 7.0.0 `win.rs` uses dirs-sys KnownFolder APIs
(`SHGetKnownFolderPath`), not APPDATA/LOCALAPPDATA. Setting environment variables
would not safely redirect actual app_data_dir or WebView2 storage. This lane does
not launch the executable or touch the actual runner user profile. A future
supported disposable OS-user containment design plus independently tested native
UI ownership is required; no registry/profile rewrite is authorized here.

**macOS:** dirs-sys HOME behavior can relocate Rust settings, but this does not
establish WKWebView/CFPreferences/native service containment. Complete user-profile
containment plus owned accessibility/window close must be proven first. The lane
archives the complete bundle and records UNRUN, never assumes HOME is enough or
requests Accessibility/Screen Recording permissions by a shortcut.

**Local Linux:** actual default custom-protocol Debian build/extraction succeeds.
The runtime command stops at `unshare: unshare failed: Operation not permitted`.
No application is started, no root/sandbox fallback is attempted, and therefore
normal launch/close/restart/settings/native screenshots remain UNRUN locally.
The previous original WebKit namespace-denial evidence is not superseded.
Local tooling is Rust1.99.1 / Node26.12.1, explicitly **not** canonical Node24
hosted execution. Hosted three-OS builds/runtime remain UNRUN until owner push.

## Host tests and evidence limits

`default-test.py` retains a reviewed ordered named inventory and requires every
actual success, zero failures/errors/skips and no missing/duplicate IDs. Test-first
RED logs cover inventory, lifecycle, isolation, packager and receipts. Positive
lifecycle uses a clearly labeled fake adapter; negatives cover wrong PID/path,
stale evidence, early exit, timeout even with zero cleanup, exit-zero-without-proof
and blank capture. Native close API positive/negative doubles additionally verify
EWMH identity refusal; these are not actual native-window events. Real synthetic
portable/macOS archives test roundtrip layout, not PE/Mach-O execution.

Implementation evidence lives under ignored `rust/test-artifacts/default-development`:
raw RED/GREEN logs, local build logs, original failed attempts and package/runtime
receipts. The task handoff binds final source commit/tree/hashes and code/evidence
archives. Runtime proof, independent Nyx review and hosted campaign are subsequent
owner gates. No signing/notarization, installation, upgrade/uninstall, mouse,
screen-reader usability, Wayland/HiDPI, audio/GPU, live accounts/media, final release
or whole-application completeness is claimed.


## Inner cleanup status correction
Correctedoutercandidate8f5836ac/cec93d0a remains BLOCKED on MINOR NYX-DP-03: unchanged LinuxNative.cleanup also passed its direct Popen child to psutil.wait_procs, reporting0 instead of actualSIGTERM-15. Original whole report and evidence remain unchanged. Architecture assessment confirms exclusive outer subreaper ownership is sound; the remaining fix is the same single wait-owner rule at the inner adapter, not another architecture or safety change.

Inner cleanup now removes the direct child from psutil wait lists, lets Popen alone wait/reap it and escalates an unresponsive direct child through its retained Popen handle before recording its real returncode. Real exact-owned sleeper regression went RED0!=-15 to GREEN;25closednamedhosttests PASS normal/-O/-OO, four outer supervisor tests and existing three package tests remain green. New regression enumerates only its own exact fixture; no native application, peer or credential use. Corrected source reassessment pending; native all platforms remains UNRUN. Source-reported diagnostic integrity is not GUI acceptance or proof of audible output.

## Historical bounded correction status
Originalb2b869bf source review BLOCKED on NYX-DP-01/02 and remains immutable. The new supervisor uses exclusive process-local kernel subreaper ownership, not a polling speed change, host-root/cgroup permission or process-name kill. It refuses unsupported platforms, concurrent thread owners and preexisting children before launch. Actual current raw timeout exit is reaped only by Popen and retained. No installed application/Windows/macOS/native-Linux acceptance follows from the four host-process regressions or24hostcontracts.

The original local debug package receipt predates the final source-commit fields; it is build/static evidence bound through preserved source-byte parity, not a receipt generated by the final committed tool. Any hosted package must carry the actual compiler-feature/source commit/tree receipt from that run. Status-name/AT-SPI/seed-versus-save concerns remain unverified native risks; one future hosted campaign must report actual failures rather than silently waiving them. No changed production files or existing engineering workflow.
