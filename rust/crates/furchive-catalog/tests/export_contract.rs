use std::io::Write;
fn gzip(csv: &str) -> Vec<u8> {
    let mut gzip = flate2::write::GzEncoder::new(Vec::new(), flate2::Compression::default());
    gzip.write_all(csv.as_bytes()).unwrap();
    gzip.finish().unwrap()
}
#[test]
fn pool_export_stream_handles_quoted_newlines_and_authoritative_order() {
    let data = gzip(
        "id,name,description,is_active,post_ids\n7,comic,\"line one\nline two\",t,\"{30,2,10}\"\n",
    );
    let mut received = Vec::new();
    furchive_catalog::read_pools_gzip(
        data.as_slice(),
        furchive_catalog::Limits::default(),
        |pool| {
            received.push(pool);
            Ok(())
        },
    )
    .unwrap();
    assert_eq!(received.len(), 1);
    assert_eq!(received[0].post_ids, vec!["30", "2", "10"]);
    assert_eq!(
        received[0].description.as_deref(),
        Some("line one\nline two")
    );
}
