# Product browsing native attempt1

Candidate14c984ec57c25113182f49d4c4520b3a6086d73a/run43317327689. NativeLinux/Windows and all3download jobs SUCCESS; macOS native FAILED. Overall candidate is not accepted on all platforms.

Owner downloaded Linux/Windows artifactZIPs, verified provider SHA256, read back actual both browser-phase receipts (7+3checks each), HTTP request inventory and sourceCommit/binary bindings. Actual product controls -> TauriIPC -> Rust -> bounded local metadata HTTP -> SQLite restart all passed. No live artwork/account/media credential used. Query readback shows fox -comic rating:s order:id_desc retained across page2 despite unsent wolf chip; favorites adds explicit fav:fixture_user; pool exactID10,2,30 preserves2unavailable holes. Both search/pin restored on restart; delete/unpin verified persisted empty by runner. Linux binary0812a8ea465a18cfd22415708ea00484d7ceaea38ef170a6fce36dba0fdf4f9b; Windows11c4ba8af94cd77e12e5e369100e460e53598d79f2f0459034139d34cfcd03d8.

macOS never reached product campaign: media runtime reported cleanupcycle0 staleIgnoredfalse (remaining0/releasedtrue), other19cycles true, gallery925ms/all3videos successful. Prior4d78636 remains bounded native media PASS; that does not override current candidate failure. Actual artifacts retained under rust/test-artifacts/ci-run-43317327689.

## Observer race diagnosis

media-campaign clicked still.png then waited only `viewer.status==='ready'`. At that instant previous selected image can still be ready before Svelte flush; `currentMedia()` captures that previous node, misclassifying ensuing genuine replacement as stale-event handling failure. Need await selected identity AND distinct node AND ready after tick before injecting old node events. This does not modify production Viewer cleanup or weaken staleIgnored/20cycle acceptance.

New compiled realViewer regression delays replacement while old node ready. Narrow missingobserverRED then GREEN. Deliberately restoring old ready-only predicate yields exact reference-equality assertion FAIL; corrected predicateGREEN. Failed mutation's pending timer caused a secondary asynchronous test diagnostic; added test-owned timer cleanup, final all12compiledcomponenttests clean.13unit/check0errors0warnings/VitePASS. Current native correction pending new exact commit CI. No skipped lifecyclecycle, fabricated receipt, raised threshold, added retry or changed production media behavior.

Full app incomplete: production media/cache/download integration, credentials/settings/migrationUI and broaderOS/architecture/accessibility/audio gates remain outstanding.
