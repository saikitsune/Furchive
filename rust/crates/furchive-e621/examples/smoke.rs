#[tokio::main]
async fn main() -> Result<(), String> {
    let api = furchive_e621::E621Client::new()?;
    let query = furchive_domain::SearchQuery {
        include: vec!["fox".into()],
        ..Default::default()
    };
    let result = api.search(&query, 1, 2).await?;
    println!(
        "Live e621 identified-UA smoke: {} safe metadata records, raw_count={}, next_page={:?}",
        result.items.len(),
        result.raw_count,
        result.next_page
    );
    if result
        .items
        .iter()
        .any(|p| p.rating != furchive_domain::Rating::Safe)
    {
        return Err("safe filter contract failed".into());
    }
    Ok(())
}
