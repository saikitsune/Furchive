# Native media attempt3 — transport and mute-intent repairs

Candidate7c4c399/run43285045813: all3download jobs PASS; Windows full native/package campaign PASS; Linux media runtime muted-state FAIL; macOS native socket unit test FAIL before the package campaign. Prior failures preserved, no overall media PASS.

## macOS exact root cause and deterministic reproduction

Native transport diagnostic says WouldBlock, followed by client ConnectionReset in fixed_media_streams_exact_ranges_and_head_without_filesystem_scope. Listener is nonblocking for bounded shutdown, but accepted-stream blocking state was never explicitly set. BSD/macOS can inherit the listener's nonblocking flag, unlike Linux. Read deadlines do not clear O_NONBLOCK. This explains a legitimate request sometimes losing its connection before its full headers arrive; previous metadata-ready then resource-reset observations are compatible, but that earlier attribution is inference, not traced proof.

Added accepted_nonblocking_socket_is_normalized_before_reading_split_headers: actual client sends split headers, server socket explicitly nonblocking as a portable failure injection. Observed RED immediately, then GREEN after serve sets only this owned connection blocking before its existing750ms read/write and1500ms whole-header deadlines. No deadline changes, hidden retry, fake HTTP or request-scope widening. Existing Host/origin/token/fixed-resource/Range contract unchanged. POSIX portability reference: https://man7.org/linux/man-pages/man2/accept.2.html notes applications should explicitly set required accepted-socket flags rather than rely on inheritance.

## Linux exact result and correction

Actual run retains defaultMuted:true before and after loading, yet current muted changes true->false during playback/seek. The previous default initialization repair was insufficient; do not claim it fixed native Linux. UI displayed intent remains muted. New component boundary test explicitly changes the media element as a native pipeline might and dispatches volumechange: RED because intent was lost. Added generation-fenced volumechange/play/seeked reconciliation to the current explicit UI mute intent. GREEN for initial muted intent AND user unmute (native reset cannot override either); no permanent forced-mute policy, no privileged bypass. This models the application-owned synchronization invariant, not WebKit internals. Corrected actual Linux execution remains pending.

## Local verification of this exact repair

12unit tests,7compiled Svelte DOM tests,0Svelte errors/warnings,Vite build PASS.5native Rust tests including new real TCP split-header test PASS. Rustfmt and strict all-target Clippy default/self-test+custom-protocol PASS. Browser/native Windows/macOS verification is not inferred from Linux. Full application, product UI and cross-platform media acceptance remain incomplete. Next existing three-platform CI run must exercise all unchanged required media assertions and real captured frames; no new worker graph.
