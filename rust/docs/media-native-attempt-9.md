# Native media attempt9 — real AppKit geometry works, performance remains nonpass

Candidate7bd9ad4/run43299872426. Linux/Windows native jobs SUCCESS; Linux/Windows download jobs SUCCESS. macOS native campaign FAIL; macOS download job was cancelled without running to completion, NOT counted PASS for this run.

## New actual observations

AppKit measured full WKWebView1024x681, safe1024x653 with native top inset28,flippedtrue,scale1. These values match JavaScript viewport exactly and allowed allthree animation checkpoints. Real runtime verifies every video plays/seeks/muted/releases and20rapid navigation/close/stale-event cycles. No remaining decode or geometry mismatch demonstrated. Overall macOS receipt fails unchanged initial-render<=1000ms requirement at1177ms, not functional playback.

Raw macOS frame samples also have multiple209–230ms intervals (strict upper bound200). Since runtime gate stopped first, host-frame acceptance did not execute; actual frames are retained, not accepted by fiat. Capture API currently asks CoreGraphics to composite the whole onscreen window list through mss; per-sample foreground list plus full desktop composition consumes host budget. This is driver overhead, not an app FPS benchmark; strict cadence still mandatory.

## Performance correction without changing acceptance

The harness built/validated the same10000-item array initially, rebuilt it when native video-base handshake changed unrelated grants, and Gallery validated/copied it again. Make static collection/grants independent of video handshake; retain validated deeply-frozen outputs in a weak identity map keyed to a freshly computed exact-grant signature. Reuse ONLY own immutable validated outputs, never caller mutable arrays. Grant revocation/mutable inputs are revalidated. Specific new regression observed RED identity-copy then GREEN identity reuse+revocation refusal+mutable source refusal. This removes repeated URL validation/freezing without skipping initial security checks. Local Node measurement first10000validation7.61ms/100frozenreuses0.057ms; explicitly not a native render result. Native1177ms campaign remains failed until corrected actual run meets1000ms, not threshold raised.

For macOS host capture, target the independently foreground-verified owned CGWindowID via kCGWindowListOptionIncludingWindow and exact native-safe-client region, rather than all desktop composition. Preserve every-sample foreground PID/window check, physical dimensions,36rawPNG hashes,12frames and30–200ms cadence. New labeled Quartz-call adapter test observed missing seamRED then GREEN: exact selected window ID/rect/options and BGRA pixel conversion; not native proof. No captured substitute image, retry loop or tolerance change. Actual sampling speed and pixel-state progression must still pass hosted macOS.

Local13unit+9compiledcomponent tests,Svelte0errors0warnings,VitePASS. Python2driver+1parser+1corpus testsPASS. Source-only/native host limits unchanged; no sandbox/policy/permission changes. Full app/product UI and media gate still incomplete.
