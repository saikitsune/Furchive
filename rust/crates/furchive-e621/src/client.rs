use std::sync::Arc;
use std::time::Duration;
use tokio::sync::Mutex;
use tokio::time::Instant;

#[derive(Clone)]
pub struct E621Client {
    http: reqwest::Client,
    base: url::Url,
    next_request: Arc<Mutex<Instant>>,
    pace: Duration,
    account: Option<Arc<crate::Account>>,
}
impl E621Client {
    pub fn new() -> Result<Self, String> {
        Self::build("https://e621.net/", Duration::from_secs(1))
    }
    #[cfg(feature = "fixture-api")]
    pub fn local_fixture(base: &str) -> Result<Self, String> {
        let url = url::Url::parse(base).map_err(|_| "invalid fixture origin")?;
        if url.scheme() != "http"
            || url.host_str() != Some("127.0.0.1")
            || url.port().is_none()
            || !url.username().is_empty()
            || url.password().is_some()
            || url.path() != "/"
            || url.query().is_some()
            || url.fragment().is_some()
        {
            return Err("fixture API requires an exact IPv4 loopback origin".into());
        }
        Self::build(base, Duration::from_millis(1))
    }
    fn build(base: &str, pace: Duration) -> Result<Self, String> {
        let http = reqwest::Client::builder()
            .user_agent("Furchive/2.0-alpha (+https://github.com/saikitsune/Furchive)")
            .redirect(reqwest::redirect::Policy::none())
            .timeout(Duration::from_secs(30))
            .build()
            .map_err(|_| "HTTP client initialization failed")?;
        Ok(Self {
            http,
            base: url::Url::parse(base).map_err(|_| "invalid API origin")?,
            next_request: Arc::new(Mutex::new(Instant::now())),
            pace,
            account: None,
        })
    }
    pub fn with_account(mut self, account: crate::Account) -> Self {
        self.account = Some(Arc::new(account));
        self
    }
    pub fn without_account(mut self) -> Self {
        self.account = None;
        self
    }
    #[cfg(test)]
    fn test_client(base: &str) -> Self {
        Self::build(base, Duration::ZERO).unwrap()
    }
    async fn get_json(
        &self,
        path: &str,
        pairs: &[(&str, String)],
    ) -> Result<serde_json::Value, String> {
        let mut url = self.base.join(path).map_err(|_| "invalid API path")?;
        url.query_pairs_mut()
            .extend_pairs(pairs.iter().map(|(k, v)| (*k, v.as_str())));
        let mut response = {
            let mut attempt = 0u32;
            loop {
                let mut next = self.next_request.lock().await;
                tokio::time::sleep_until(*next).await;
                *next = Instant::now() + self.pace;
                let mut request = self.http.get(url.clone());
                if let Some(account) = &self.account {
                    request = request.basic_auth(account.username(), Some(account.key()));
                }
                let response = request
                    .send()
                    .await
                    .map_err(|_| "e621 request failed (network or timeout)")?;
                if matches!(response.status().as_u16(), 429 | 503) && attempt < 3 {
                    let seconds = response
                        .headers()
                        .get("Retry-After")
                        .and_then(|v| v.to_str().ok())
                        .and_then(|s| s.parse::<u64>().ok())
                        .unwrap_or(1u64 << attempt);
                    if seconds > 120 {
                        return Err(format!("e621 requires a {seconds}s cooldown; retry later"));
                    }
                    *next = Instant::now() + Duration::from_secs(seconds).max(self.pace);
                    attempt += 1;
                    continue;
                }
                break response;
            }
        };
        if !response.status().is_success() {
            return Err(format!("e621 returned HTTP {}", response.status().as_u16()));
        }
        const LIMIT: usize = 16 * 1024 * 1024;
        if response.content_length().is_some_and(|n| n > LIMIT as u64) {
            return Err("e621 response exceeds metadata limit".into());
        }
        let mut bytes = Vec::new();
        while let Some(chunk) = response
            .chunk()
            .await
            .map_err(|_| "e621 response interrupted")?
        {
            if chunk.len() > LIMIT.saturating_sub(bytes.len()) {
                return Err("e621 response exceeds metadata limit".into());
            }
            bytes.extend_from_slice(&chunk);
        }
        serde_json::from_slice(&bytes).map_err(|_| "invalid e621 JSON response".into())
    }
    pub async fn verify_identity(&self) -> Result<crate::VerifiedIdentity, String> {
        let account = self
            .account
            .as_ref()
            .ok_or("no account credentials configured")?;
        let value = self.get_json("users/me.json", &[]).await?;
        let identity: crate::VerifiedIdentity =
            serde_json::from_value(value).map_err(|_| "invalid account identity response")?;
        if identity.id == 0 || !identity.name.eq_ignore_ascii_case(account.username()) {
            return Err("e621 identity did not match configured account".into());
        }
        Ok(identity)
    }
    pub async fn pool(&self, id: u64) -> Result<furchive_domain::Pool, String> {
        if id == 0 {
            return Err("invalid pool ID".into());
        }
        crate::map_pool(&self.get_json(&format!("pools/{id}.json"), &[]).await?)
    }
    /// Exact requested membership only; absent/account-filtered members stay absent.
    /// This endpoint must never introduce filter_id=0 or compact pool positions.
    pub async fn posts_by_ids(
        &self,
        ids: &[String],
    ) -> Result<Vec<furchive_domain::MediaItem>, String> {
        let mut requested = std::collections::HashSet::new();
        if ids.is_empty() || ids.len() > 100 {
            return Err("post batch must contain 1–100 IDs".into());
        }
        for id in ids {
            let number = id.parse::<u64>().map_err(|_| "invalid post ID")?;
            if number == 0 || number.to_string() != *id || !requested.insert(id.as_str()) {
                return Err("invalid or duplicate post ID".into());
            }
        }
        let value = self
            .get_json(
                "posts.json",
                &[
                    ("tags", format!("id:{}", ids.join(","))),
                    ("limit", ids.len().to_string()),
                ],
            )
            .await?;
        let rows = value["posts"].as_array().ok_or("missing posts array")?;
        if rows.len() > ids.len() {
            return Err("unexpected post batch size".into());
        }
        let mut seen = std::collections::HashSet::new();
        let mut posts = Vec::new();
        for row in rows {
            let post = crate::map_post(row)?;
            if !requested.contains(post.key.id()) || !seen.insert(post.key.id().to_owned()) {
                return Err("unrequested or duplicate batch member".into());
            }
            posts.push(post);
        }
        Ok(posts)
    }

    pub async fn suggestions(
        &self,
        prefix: &str,
        limit: usize,
    ) -> Result<Vec<crate::TagSuggestion>, String> {
        if prefix.len() < 2 {
            return Ok(Vec::new());
        }
        if prefix.len() > 100 || prefix.chars().any(char::is_control) || !(1..=30).contains(&limit)
        {
            return Err("invalid tag suggestion query".into());
        }
        crate::map_suggestions(
            self.get_json(
                "tags.json",
                &[
                    ("search[name_matches]", format!("{prefix}*")),
                    ("limit", limit.to_string()),
                ],
            )
            .await?,
        )
    }
    pub async fn search(
        &self,
        query: &furchive_domain::SearchQuery,
        page: u32,
        limit: usize,
    ) -> Result<crate::SearchPage, String> {
        if page == 0 || !(1..=100).contains(&limit) {
            return Err("invalid page or limit".into());
        }
        let tags = query.e621_tags().map_err(|e| e.to_string())?;
        let value = self
            .get_json(
                "posts.json",
                &[
                    ("tags", tags),
                    ("page", page.to_string()),
                    ("limit", limit.to_string()),
                ],
            )
            .await?;
        crate::parse_search_page(value, query, page, limit)
    }
}

#[cfg(test)]
mod tests {
    #[cfg(feature = "fixture-api")]
    #[test]
    fn fixture_api_is_strict_loopback_only_without_account_or_redirect_scope() {
        assert!(super::E621Client::local_fixture("http://127.0.0.1:43123/").is_ok());
        for url in [
            "https://e621.net/",
            "http://localhost:43123/",
            "http://127.0.0.1/",
            "http://user@127.0.0.1:43123/",
            "http://127.0.0.1:43123/path",
            "http://127.0.0.1:43123/?filter_id=0",
            "http://127.0.0.1:43123/#fragment",
        ] {
            assert!(super::E621Client::local_fixture(url).is_err(), "{url}");
        }
    }

    #[tokio::test]
    async fn exact_post_batch_retains_filters_and_rejects_unrequested_duplicate_ids() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            for body in [
                r#"{"posts":[{"id":2,"rating":"s"},{"id":10,"rating":"q"}]}"#,
                r#"{"posts":[{"id":99,"rating":"s"}]}"#,
                r#"{"posts":[{"id":2,"rating":"s"},{"id":2,"rating":"s"}]}"#,
            ] {
                let (mut stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                    .unwrap();
                let mut bytes = [0; 4096];
                let n = stream.read(&mut bytes).unwrap();
                let request = String::from_utf8_lossy(&bytes[..n]);
                let target = request.split_whitespace().nth(1).unwrap();
                let url = url::Url::parse(&format!("http://fixture{target}")).unwrap();
                let pairs: std::collections::HashMap<_, _> =
                    url.query_pairs().into_owned().collect();
                assert_eq!(pairs.get("tags").unwrap(), "id:10,2,30");
                assert_eq!(pairs.get("limit").unwrap(), "3");
                assert!(!pairs.contains_key("filter_id"));
                write!(
                    stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                )
                .unwrap();
            }
        });
        let client = super::E621Client::test_client(&base);
        let ids = vec!["10".into(), "2".into(), "30".into()];
        let posts = client.posts_by_ids(&ids).await.unwrap();
        assert_eq!(
            posts.iter().map(|p| p.key.id()).collect::<Vec<_>>(),
            vec!["2", "10"]
        );
        assert!(client.posts_by_ids(&ids).await.is_err());
        assert!(client.posts_by_ids(&ids).await.is_err());
        server.join().unwrap();
        for invalid in [
            vec![],
            vec!["1".into(), "1".into()],
            vec!["0".into()],
            vec!["1 rating:e".into()],
            vec!["01".into()],
            vec!["-3".into()],
            vec!["1".into(); 101],
        ] {
            assert!(client.posts_by_ids(&invalid).await.is_err());
        }
    }

    #[tokio::test]
    async fn verified_identity_requires_current_account_match() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            for name in ["someone_else", "fixture_user"] {
                let (mut stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                    .unwrap();
                let mut bytes = [0; 4096];
                let n = stream.read(&mut bytes).unwrap();
                assert!(String::from_utf8_lossy(&bytes[..n]).starts_with("GET /users/me.json"));
                let body = format!("{{\"id\":123,\"name\":\"{name}\"}}");
                write!(
                    stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                )
                .unwrap();
            }
        });
        let client = super::E621Client::test_client(&base).with_account(
            crate::Account::new("fixture_user".into(), "fixture_key".into()).unwrap(),
        );
        assert!(client.verify_identity().await.is_err());
        assert_eq!(client.verify_identity().await.unwrap().name, "fixture_user");
        server.join().unwrap();
    }
    #[tokio::test]
    async fn logout_removes_authorization_from_future_requests() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let mut requests = Vec::new();
            for _ in 0..2 {
                let (mut stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                    .unwrap();
                let mut bytes = [0; 4096];
                let n = stream.read(&mut bytes).unwrap();
                requests.push(String::from_utf8_lossy(&bytes[..n]).into_owned());
                let body = r#"{"posts":[]}"#;
                write!(
                    stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                )
                .unwrap();
            }
            requests
        });
        let client = super::E621Client::test_client(&base).with_account(
            crate::Account::new("fixture_user".into(), "fixture_key".into()).unwrap(),
        );
        client.search(&Default::default(), 1, 50).await.unwrap();
        client
            .without_account()
            .search(&Default::default(), 1, 50)
            .await
            .unwrap();
        let req = server.join().unwrap();
        assert!(req[0].to_lowercase().contains("authorization: basic"));
        assert!(!req[1].to_lowercase().contains("authorization:"));
        assert!(!req[0].lines().next().unwrap().contains("fixture_key"));
    }

    #[tokio::test]
    async fn pool_and_autocomplete_use_same_real_client_boundary() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let mut requests = Vec::new();
            for body in [
                r#"{"id":7,"name":"pool","post_ids":[30,2,10]}"#,
                r#"[{"name":"fox","category":5,"post_count":8}]"#,
            ] {
                let (mut stream, _) = listener.accept().unwrap();
                let mut bytes = [0; 4096];
                let n = stream.read(&mut bytes).unwrap();
                requests.push(String::from_utf8_lossy(&bytes[..n]).into_owned());
                write!(
                    stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                    body.len()
                )
                .unwrap();
            }
            requests
        });
        let client = super::E621Client::test_client(&base);
        assert_eq!(
            client.pool(7).await.unwrap().post_ids,
            vec!["30", "2", "10"]
        );
        assert_eq!(client.suggestions("fo", 10).await.unwrap()[0].name, "fox");
        let requests = server.join().unwrap();
        assert!(requests[0].starts_with("GET /pools/7.json"));
        assert!(requests[1].contains("name_matches"));
    }
    #[tokio::test]
    async fn rate_limited_response_retries_after_then_succeeds() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            for status in [429, 200] {
                let (mut stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                    .unwrap();
                let mut b = [0; 4096];
                let n = stream.read(&mut b).unwrap();
                assert!(n > 0);
                let body = if status == 200 { r#"{"posts":[]}"# } else { "" };
                write!(stream,"HTTP/1.1 {status} OK\r\nContent-Length: {}\r\nRetry-After: 0\r\nConnection: close\r\n\r\n{body}",body.len()).unwrap();
            }
        });
        let result = super::E621Client::test_client(&base)
            .search(&furchive_domain::SearchQuery::default(), 1, 50)
            .await;
        assert!(result.is_ok(), "{result:?}");
        server.join().unwrap();
    }

    #[tokio::test]
    async fn oversized_declared_metadata_is_rejected_before_body_read() {
        use std::io::Write;
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            {
                use std::io::Read;
                let mut request = [0; 4096];
                let n = stream.read(&mut request).unwrap();
                assert!(n > 0);
            }
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: 16777217\r\nConnection: close\r\n\r\n"
            )
            .unwrap();
        });
        let result = super::E621Client::test_client(&base)
            .search(&furchive_domain::SearchQuery::default(), 1, 50)
            .await
            .unwrap_err();
        assert_eq!(result, "e621 response exceeds metadata limit");
        server.join().unwrap();
    }
    #[tokio::test]
    async fn search_requests_identify_client_without_filter_override_or_query_secrets() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let base = format!("http://{}/", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(std::time::Duration::from_secs(4)))
                .unwrap();
            let mut b = [0; 4096];
            let n = stream.read(&mut b).unwrap();
            let text = String::from_utf8_lossy(&b[..n]).to_string();
            let body = r#"{"posts":[{"id":9,"rating":"s"}]}"#;
            write!(
                stream,
                "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
                body.len(),
                body
            )
            .unwrap();
            text
        });
        let client = super::E621Client::test_client(&base);
        let result = client
            .search(&furchive_domain::SearchQuery::default(), 1, 50)
            .await
            .unwrap();
        assert_eq!(result.items[0].key.id(), "9");
        let req = server.join().unwrap();
        assert!(req.to_lowercase().contains("user-agent: furchive/"));
        assert!(req.contains("rating%3As"));
        assert!(!req.contains("api_key"));
        assert!(!req.contains("filter_id"));
    }
}
