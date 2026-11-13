//! Durable single-download engine. See README for the supported platform contract.
#![doc = include_str!("../README.md")]
#[cfg(not(any(target_os = "linux", target_os = "macos", windows)))]
compile_error!("furchive-downloads requires Linux, macOS or Windows native capabilities");

mod files;
mod metadata;
mod model;
mod worker;
pub use metadata::{MetadataStatus, NormalizedMetadata, METADATA_LIMIT};
pub use model::*;
pub use worker::{Control, Worker};

#[cfg(test)]
mod tests;
