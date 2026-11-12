//! Coordinate-only helpers; no display or screenshot authority.
#[cfg(any(test, all(feature = "self-test", target_os = "macos")))]
pub fn safe_crop(
    bounds: [f64; 4],
    safe: [f64; 4],
    flipped: bool,
    scale: f64,
) -> Result<[f64; 4], String> {
    if !bounds
        .iter()
        .chain(safe.iter())
        .chain(std::iter::once(&scale))
        .all(|n| n.is_finite())
        || scale <= 0.0
        || bounds[2] <= 0.0
        || bounds[3] <= 0.0
        || safe[2] <= 0.0
        || safe[3] <= 0.0
        || safe[0] < bounds[0]
        || safe[1] < bounds[1]
        || safe[0] + safe[2] > bounds[0] + bounds[2]
        || safe[1] + safe[3] > bounds[1] + bounds[3]
    {
        return Err("invalid native safe-area crop".into());
    }
    let x = safe[0] - bounds[0];
    let y = if flipped {
        safe[1] - bounds[1]
    } else {
        bounds[1] + bounds[3] - safe[1] - safe[3]
    };
    Ok([x * scale, y * scale, safe[2] * scale, safe[3] * scale])
}
#[cfg(test)]
mod tests {
    use super::safe_crop;
    #[test]
    fn appkit_safe_area_uses_native_inset_not_guessed_title_height() {
        assert_eq!(
            safe_crop([0., 0., 1024., 684.], [0., 0., 1024., 656.], false, 1.),
            Ok([0., 28., 1024., 656.])
        );
        assert_eq!(
            safe_crop([0., 0., 1024., 684.], [0., 28., 1024., 656.], true, 2.),
            Ok([0., 56., 2048., 1312.])
        );
        assert!(safe_crop([0., 0., 1024., 684.], [0., 0., 1025., 656.], false, 1.).is_err());
        assert!(
            safe_crop(
                [0., 0., 1024., 684.],
                [0., 0., 1024., 656.],
                false,
                f64::NAN
            )
            .is_err()
        );
        assert!(safe_crop([0., 0., 1024., 684.], [0., 0., 1024., 656.], false, 0.).is_err());
    }
}
