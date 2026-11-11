use super::*;
use std::io::{self, Read};

#[test]
fn actual_reader_is_stopped_at_limit_plus_one_without_metadata() {
    struct Growing(usize);
    impl Read for Growing {
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            self.0 += bytes.len();
            assert!(self.0 <= 1025, "unbounded reader consumption");
            bytes.fill(b'x');
            Ok(bytes.len())
        }
    }
    assert!(bounded_read(Growing(0), 1024).is_err());
    assert_eq!(bounded_read(&b"abc"[..], 3).unwrap(), b"abc");
}

#[test]
fn wal_header_only_and_uncommitted_tail_are_refused() {
    let mut main = vec![0u8; 100];
    main[..16].copy_from_slice(b"SQLite format 3\0");
    main[16..18].copy_from_slice(&512u16.to_be_bytes());
    // Exercise both SQLite checksum byte orders with real wire-format frames.
    for little in [true, false] {
        let checksum = |bytes: &[u8], mut s: [u32; 2]| {
            for p in bytes.as_chunks::<8>().0 {
                let word = |b: &[u8]| {
                    let a = [b[0], b[1], b[2], b[3]];
                    if little {
                        u32::from_le_bytes(a)
                    } else {
                        u32::from_be_bytes(a)
                    }
                };
                s[0] = s[0].wrapping_add(word(&p[..4])).wrapping_add(s[1]);
                s[1] = s[1].wrapping_add(word(&p[4..])).wrapping_add(s[0]);
            }
            s
        };
        let mut wal = vec![0u8; 32 + 24 + 512];
        wal[..4]
            .copy_from_slice(&(if little { 0x377f0682u32 } else { 0x377f0683u32 }).to_be_bytes());
        wal[4..8].copy_from_slice(&3_007_000u32.to_be_bytes());
        wal[8..12].copy_from_slice(&512u32.to_be_bytes());
        let h = checksum(&wal[..24], [0, 0]);
        wal[24..28].copy_from_slice(&h[0].to_be_bytes());
        wal[28..32].copy_from_slice(&h[1].to_be_bytes());
        assert!(wal::validate(&main, &wal[..32]).is_err());
        wal[32..36].copy_from_slice(&1u32.to_be_bytes());
        let s = checksum(&wal[56..], checksum(&wal[32..40], h));
        wal[48..52].copy_from_slice(&s[0].to_be_bytes());
        wal[52..56].copy_from_slice(&s[1].to_be_bytes());
        assert!(wal::validate(&main, &wal).is_err());
        wal[36..40].copy_from_slice(&1u32.to_be_bytes());
        let s = checksum(&wal[56..], checksum(&wal[32..40], h));
        wal[48..52].copy_from_slice(&s[0].to_be_bytes());
        wal[52..56].copy_from_slice(&s[1].to_be_bytes());
        assert!(wal::validate(&main, &wal).is_ok());
    }
}
