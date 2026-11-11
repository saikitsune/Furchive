//! Conservative WAL admission. SQLite intentionally ignores invalid WAL tails;
//! recovery must instead fail closed, not silently omit legacy user state.
pub(super) fn validate(main: &[u8], wal: &[u8]) -> Result<(), String> {
    if wal.is_empty() {
        return Ok(());
    }
    let bad = || "legacy WAL invalid or incomplete; manual recovery required".to_owned();
    if main.len() < 100 || &main[..16] != b"SQLite format 3\0" || wal.len() < 32 {
        return Err(bad());
    }
    let be = |b: &[u8]| u32::from_be_bytes([b[0], b[1], b[2], b[3]]);
    let little = match be(&wal[..4]) {
        0x377f0682 => true,
        0x377f0683 => false,
        _ => return Err(bad()),
    };
    if be(&wal[4..8]) != 3_007_000 {
        return Err(bad());
    }
    let page = be(&wal[8..12]) as usize;
    let db_page = u16::from_be_bytes([main[16], main[17]]) as usize;
    let db_page = if db_page == 1 { 65536 } else { db_page };
    if !(512..=65536).contains(&page)
        || !page.is_power_of_two()
        || page != db_page
        || !(wal.len() - 32).is_multiple_of(page + 24)
    {
        return Err(bad());
    }
    let checksum = |bytes: &[u8], mut s: [u32; 2]| {
        for pair in bytes.as_chunks::<8>().0 {
            let word = |b: &[u8]| {
                if little {
                    u32::from_le_bytes([b[0], b[1], b[2], b[3]])
                } else {
                    be(b)
                }
            };
            s[0] = s[0].wrapping_add(word(&pair[..4])).wrapping_add(s[1]);
            s[1] = s[1].wrapping_add(word(&pair[4..])).wrapping_add(s[0]);
        }
        s
    };
    let mut sum = checksum(&wal[..24], [0, 0]);
    if sum != [be(&wal[24..28]), be(&wal[28..32])] {
        return Err(bad());
    }
    let mut committed = false;
    for frame in wal[32..].chunks_exact(page + 24) {
        if be(&frame[..4]) == 0 || frame[8..16] != wal[16..24] {
            return Err(bad());
        }
        sum = checksum(&frame[..8], sum);
        sum = checksum(&frame[24..], sum);
        if sum != [be(&frame[16..20]), be(&frame[20..24])] {
            return Err(bad());
        }
        committed = be(&frame[4..8]) != 0;
    }
    if !committed {
        return Err(bad());
    }
    Ok(())
}
