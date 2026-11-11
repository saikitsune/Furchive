pub fn select_range(range: Option<&str>, len: usize) -> Result<(u16, usize, usize), ()> {
    let Some(range) = range else {
        return Ok((200, 0, len));
    };
    let (start, end) = range
        .strip_prefix("bytes=")
        .ok_or(())?
        .split_once('-')
        .ok_or(())?;
    if start.is_empty() {
        let suffix: usize = end.parse().map_err(|_| ())?;
        return if suffix == 0 || len == 0 {
            Err(())
        } else {
            Ok((206, len.saturating_sub(suffix), len))
        };
    }
    let start: usize = start.parse().map_err(|_| ())?;
    let end = if end.is_empty() {
        len
    } else {
        end.parse::<usize>()
            .map_err(|_| ())?
            .checked_add(1)
            .ok_or(())?
            .min(len)
    };
    if start >= len || end <= start {
        return Err(());
    }
    Ok((206, start, end))
}

#[cfg(test)]
mod tests {
    #[test]
    fn suffix_and_invalid_ranges_follow_http_contract() {
        assert_eq!(super::select_range(Some("bytes=-3"), 10), Ok((206, 7, 10)));
        assert_eq!(super::select_range(Some("bytes=4-"), 10), Ok((206, 4, 10)));
        assert!(super::select_range(Some("bytes=10-"), 10).is_err());
        assert!(super::select_range(Some("bytes=1-2,4-5"), 10).is_err());
    }
    #[test]
    fn byte_range_returns_only_requested_segment() {
        let (status, start, end) = super::select_range(Some("bytes=2-5"), 10).unwrap();
        assert_eq!((status, start, end), (206, 2, 6));
    }
}
