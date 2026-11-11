use std::io::Write;
fn gzip(text: &str) -> Vec<u8> {
    let mut zip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    zip.write_all(text.as_bytes()).unwrap();
    zip.finish().unwrap()
}
#[test]
fn export_limits_fail_closed_for_inflation_and_extra_rows() {
    let data = gzip("id,name,post_ids\n1,pool,\"{3,2}\"\n2,second,\"{7}\"\n");
    let small = furchive_catalog::Limits {
        decompressed: 20,
        ..Default::default()
    };
    assert!(furchive_catalog::read_pools_gzip(data.as_slice(), small, |_| Ok(())).is_err());
    let one = furchive_catalog::Limits {
        rows: 1,
        ..Default::default()
    };
    assert!(furchive_catalog::read_pools_gzip(data.as_slice(), one, |_| Ok(())).is_err());
}
#[test]
fn duplicate_pool_members_are_rejected_before_callback() {
    let data = gzip("id,name,post_ids\n1,pool,\"{3,3}\"\n");
    let mut called = false;
    let result = furchive_catalog::read_pools_gzip(data.as_slice(), Default::default(), |_| {
        called = true;
        Ok(())
    });
    assert!(result.is_err());
    assert!(!called);
}
