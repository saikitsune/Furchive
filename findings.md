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

## Whole-pool admission
Whole-pool metadata holds account identity through batches/pausedcommit, with100IDs/request and10000member maximum. Exactbc7cf3d native evidence passesall3OS25+8checks; separateownerprobesverifydeactivationserializationandlater500noqueueeffects. Service refuses incomplete coverage, core validatesall beforeone transaction. Native campaign uses105members with hiddenholes at positions1/102. NYX-CONC-01 reset must follow control transitions independent of jobs polling.

## Applied presentation/cache preferences
Applied runtime preferences are distinct from editable draft, generation-fenced against late local settings reads. Gallery scale .5–2 retains <=108 tiles using row/column-aware bounds; video mute applies on next open with no autoplay. Cache preference bounds resident compressed bytes only, effective max512MiB even for legacy16GiB settings; active Arc response buffers and webview-decoded surfaces are not claimed within that resident count. Settings lock -> cache lock -> atomic persistence precedes memory/budget/eviction acknowledgment; persistence failure leaves prior runtime budget and settings unchanged. No new remote authority or session restoration.

## Account UI native qualification policy
Selftestpairedphases3/4 isolatequalification-onlynativeentry, actualUIverify/save/restart/explicitrestore/logout plusseparateprobe exactread/absence. Memoryfixturephases1/2 remain labeledseparate. LinuxprivateSecretServiceHOME/XDG anddisposablehostedWindows/macOS namespaces, no active-account authority oruserstore reuse. Hostacceptedreceipt follows successfulcleanup, exactlytwoidentityrequests and no plaintextkeysettings; realkeyringprompts/lockedstores remainopen.

## Local completed-media authority
Local bytes require currentaccount/queryauthorization and explicitnativegrant anewafterrestart; durableCompletedrecord+publicationnativeidentity+SHA256+admittedURL match before boundedread. Changed/missinglocalpublication is visibleerror, no silentnetworkfallbackconcealingcorruption. No recursivecollectionscan/arbitraryrendererpath/implicitlegacygrant. Byte snapshot goes through existingMIME/dimensionvalidation/scopedloopback/cachegeneration checks.

## Default non-self-test platform evidence
Actualrun43433712227/b9ca10f producedall3ordinarydefaultpackageswithuniqueCargoJSONfeaturecustom-protocolonly,sourcecommit/tree,andcompleteactualnestedpayload/executablehashesverified. Defaultnativeharnessrefusedbeforelaunch:hostedLinuxuid_mapwriteEPERM(with0ownedprocesssurvivors),WinKnownFoldercannotberedirectedbyAPPDATAenv,macOSHOMEdoesnotprovablycontainWKWebView/nativeprofileeffects. Thesearequalificationresource/designlimits, notthreeproductcrashes. Existing017b21cengineeringactual3OSproofisdistinctandcannotreplaceordinarynativeacceptance. Rawrefusal/failedpriorrunsandindependentsourceverdictsretained;nohostsecuritychangesperformed.

## Decisions
Desktop first; e621 only; safe first-run rating, account filters respected; no telemetry. Harmless generated media/HTTP fixtures only in repeatable tests. Native media support must be qualified per actual WebView engine.
