//! Pure domain contracts for Furchive.
pub mod category;
pub mod identity;
pub mod media;
pub mod pool;
pub mod query;
pub use category::{MediaKind, TagCategory};
pub use identity::{IdentityError, PostKey};
pub use media::MediaItem;
pub use pool::{Pool, PoolKey};
pub mod template;
pub use query::{QueryError, Rating, RatingFilter, SearchQuery, SortOrder};
pub use template::{FilenameTemplate, TemplateError, TemplateValues};
