# Furchive domain foundation

Pure Rust domain contracts; no GUI, SQLite, HTTP or filesystem writes.

- `PostKey` and `PoolKey` preserve exact `(source, id)` identity. Constructors and
  serde reject empty/whitespace/control-bearing components. Access with
  `source()` / `id()`; no implicit source fallback or ID normalization.
- `MediaItem::new(PostKey)` initializes unknown rating/kind and optional URLs.
  All normalized fields are public and serde-enabled. `tags` is a
  `BTreeMap<TagCategory, Vec<String>>`; contributor and unknown string categories
  are preserved, not moved into display categories. Pool context is separate.
- `Pool::new(PoolKey, String)` holds a full ordered `post_ids` snapshot, including
  members whose metadata is unavailable. `fetched_at` is optional Unix seconds;
  upstream created/updated dates remain optional source strings.
- `SearchQuery::default()` is Safe/Newest. Include/exclude entries are individual
  unsigned chips, not query text. Metatags are retained; `no_artist` is translated
  case-insensitively to `arttags:0` in both lists. `.e621_tags()` returns raw tags:
  the HTTP boundary must URL-encode them. Contradictory include/exclude chips are
  retained, not silently repaired. There is no remote query execution here.
- `RatingFilter::Any` includes the three known ratings, never Unknown/Restricted.
  `Only` rejects empty/unknown/restricted selections during query compilation.
  Multiple distinct ratings omit remote rating terms (which e621 would AND);
  callers **must** apply `ratings.accepts(&post.rating)` locally and use raw
  upstream counts for pagination. Unknown/restricted ratings never become Safe.
- `FilenameTemplate::parse(&str)` accepts only exact tokens `{source}`, `{artist}`,
  `{id}`, `{safeTitle}`, `{pool_name}`, `{page_number}`, `{ext}`. Both separator
  forms are normalized to `/`; rooted/UNC/drive paths, empty/dot/parent segments,
  malformed/unknown tokens and forbidden literal characters are rejected.
  `render(&TemplateValues)` sanitizes values individually, then final segments:
  whitespace/invalid characters become `_`, trailing ASCII spaces/dots are
  removed, empty becomes `_`, Windows device names get `_` prefixed. Values
  cannot inject separators or recursive tokens. Unicode is retained verbatim;
  overlong segments (>255 bytes or UTF-16 units) / paths (>1024 bytes) error.
  `safe_title` maps to the exact template spelling `safeTitle`; page_number is a
  supplied string so the caller can preserve the existing five-digit display.

## Boundaries

Template rendering is **lexical path generation only**, not a filesystem sandbox.
The actual writer must enforce granted-root confinement, symlink/junction safety,
normalization/case-fold collision policy and atomic duplicate handling. Sanitized
names may collide; this crate neither opens files nor claims unique names.
These length limits are conservative policy, not support for every filesystem or
Windows absolute path length. Strings/DTOs are normalized internal contracts, not
raw e621 JSON; the API adapter must map upstream DTOs explicitly. Query cursors,
HTTP, downloads, credential handling and desktop playback are outside this slice.

## Tests

`cargo test -p furchive-domain --locked` from `rust/` after the owner generates
`Cargo.lock`. See engine README for retained RED/GREEN and isolated runner.
