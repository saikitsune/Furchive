//! Durable single-download engine. See README for the supported platform contract.
#![doc = include_str!("../README.md")]
#[cfg(not(target_os = "linux"))]
compile_error!("furchive-downloads currently guarantees Linux dirfd confinement only");

mod files;
mod model;
mod worker;
pub use model::*;
pub use worker::{Control, Worker};

#[cfg(test)]
mod tests;
