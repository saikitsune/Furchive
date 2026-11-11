pub struct Account {
    username: String,
    key: String,
}
impl Account {
    pub fn new(username: String, key: String) -> Result<Self, String> {
        if username.is_empty()
            || key.is_empty()
            || username.len() > 100
            || key.len() > 1024
            || username.contains(':')
            || username
                .chars()
                .chain(key.chars())
                .any(|c| c.is_control() || c.is_whitespace())
        {
            return Err("invalid e621 credentials".into());
        }
        Ok(Self { username, key })
    }
    pub fn username(&self) -> &str {
        &self.username
    }
    pub(crate) fn key(&self) -> &str {
        &self.key
    }
}
impl std::fmt::Debug for Account {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Account")
            .field("username", &self.username)
            .field("key", &"[REDACTED]")
            .finish()
    }
}
