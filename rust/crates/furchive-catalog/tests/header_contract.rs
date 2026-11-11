use std::io::Write;
fn zip(text: &str) -> Vec<u8> {
    let mut out = flate2::write::GzEncoder::new(Vec::new(), Default::default());
    out.write_all(text.as_bytes()).unwrap();
    out.finish().unwrap()
}
#[test]
fn ambiguous_or_empty_headers_are_rejected_before_rows() {
    for text in [
        "id,id,name,post_ids\n1,2,pool,3\n",
        "id,name,post_ids\n1, ,3\n",
    ] {
        let data = zip(text);
        let mut seen = false;
        assert!(
            furchive_catalog::read_pools_gzip(data.as_slice(), Default::default(), |_| {
                seen = true;
                Ok(())
            })
            .is_err()
        );
        assert!(!seen);
    }
}
