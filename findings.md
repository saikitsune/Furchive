# Findings — Furchive rewrite

## Live environment
- Tool shell HOME is /opt/data/home, not passwd home /opt/data. Cargo lives at /opt/data/home/.cargo/bin, Rustup /opt/data/home/.rustup. Source "$HOME/.cargo/env" as needed; no additional updates to shared toolchain.
- Rust/Cargo 1.99.1 and rustfmt/clippy verified by scratch build/test in prior setup.
- Debian 13 x86_64, Node/npm installed, C compiler/pkg-config installed. No sudo or accessible Docker daemon; native GTK/WebKit absent.
- Existing user-owned Rust toolchains were updated during authorized setup; now pin the project and avoid changing unrelated environments.

## Architecture
Approved Tauri 2 + Svelte/TypeScript; engine UI-independent. Source review identifies data-loss-on-redownload, fake resume, incomplete schema migration, pool ordering/membership and disconnected API cache wiring. Do not port those bugs.

## Native qualification blocker
Tauri 2.12.5 executable compiled with WebKitGTK 2.54.0 from an unprivileged Debian sysroot. Native launch not qualified: WebKit helper paths require mounted prefix, and bubblewrap fails because user namespaces are prohibited by container policy. No sandbox disabling or host modification performed. GTK display can run under project-owned proot/Xvfb; that is not evidence WebKit launched.

## Concurrency decision
Native directory identity, not lowercased path spelling, reserves active writers conservatively; same-directory jobs serialize (including distinct leaf names/Rename suffixes) while separate native directories may overlap. Paused jobs reserve no writer and must not starve queued work. Worker concurrency is selected1–4 at the explicit host grant, not silently changed mid-transfer. Rates/ETA are observed samples, unknown until enough data, reset on pause/regression/removal and never restored as fabricated speed.

## Decisions
Desktop first; e621 only; safe first-run rating, account filters respected; no telemetry. Harmless generated media/HTTP fixtures only in repeatable tests. Native media support must be qualified per actual WebView engine.
