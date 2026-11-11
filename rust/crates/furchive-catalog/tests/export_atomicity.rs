use std::io::Write;
#[test]
fn invalid_gzip_crc_is_not_accepted_as_successful_import() {
    let text = "id,name,post_ids\n1,pool,\"{3,2}\"\n";
    let mut zip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    zip.write_all(text.as_bytes()).unwrap();
    let mut bytes = zip.finish().unwrap();
    let n = bytes.len();
    bytes[n - 8] ^= 1;
    assert!(
        furchive_catalog::read_pools_gzip(bytes.as_slice(), Default::default(), |_| Ok(()))
            .is_err()
    );
}
#[test]
fn duplicate_pool_rows_are_rejected_instead_of_overwriting_snapshot() {
    let text = "id,name,post_ids\n1,pool,\"{3,2}\"\n1,other,\"{7}\"\n";
    let mut zip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    zip.write_all(text.as_bytes()).unwrap();
    let bytes = zip.finish().unwrap();
    assert!(
        furchive_catalog::read_pools_gzip(bytes.as_slice(), Default::default(), |_| Ok(()))
            .is_err()
    );
}
