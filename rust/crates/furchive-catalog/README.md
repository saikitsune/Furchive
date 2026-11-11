# Bounded pool export reader

Independent nested crate while root inputs are frozen for review. `read_pools_gzip(Read,Limits,FnMut(Pool))` streams gzip/CSV and reports row count only on complete validated success. Caps compressed/decompressed bytes and rows; rejects ambiguous headers, empty names, invalid/duplicate IDs and duplicate membership. Quoted newlines and authoritative order preserved.

Callbacks can occur before a later parse/CRC/limit error. Database caller MUST use a staging transaction/table and commit only after function returns Ok; do not publish intermediate rows. This crate does not download exports, choose files, mutate databases or claim atomic catalog integration.

6 tests pass, fmt/Clippy clean. No real remote export downloaded. Limits and callback errors fail closed; unknown upstream columns ignored. Date/metadata enrichment and per-row allocation refinement remain integration work. Uses existing Rust domain model, no GUI dependencies.
