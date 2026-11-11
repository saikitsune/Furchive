# e621 client checkpoint

UI-independent identified-UA HTTPS client. Clone one client to share request pacing. Search defaults to safe, multi-rating filtering uses raw upstream count for pagination. Bounded16MiB streamed metadata, request timeout30s, no redirects, retry429/503 up to3 times, numeric Retry-After honored up to120s then surfaced for later retry. HTTP-date Retry-After, identity verification and account-epoch cancellation remain pending.

Accounts are non-serializable, Debug redacted and request Authorization only. `without_account` removes auth on that returned client; old clones still represent the old account and must be invalidated by integration. No secret persistence or authentication UI yet. Private metadata MUST NOT be written to unscoped cache. Only the anonymous client is wired to desktop at this checkpoint.

`pool`, `suggestions`, `search`, public mapping helpers preserve original tags/contributor/unknown categories and null files. This is not complete catalog export synchronization. Production origin is fixed; injectable HTTP is cfg(test) only. `examples/smoke.rs` makes a small read-only live safe metadata request, no media download.

Tests use real loopback HTTP fixtures. Parent observed RED for mapping/search/endpoint/account APIs and behavior failures for metadata limit/retry. Logout regression was added after the account boundary and passed immediately (regression coverage, not a claimed observed RED).
