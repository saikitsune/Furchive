use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
#[error("invalid portable filename template: {0}")]
pub struct TemplateError(pub String);

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FilenameTemplate(String);

#[derive(Debug, Default)]
pub struct TemplateValues<'a> {
    pub source: &'a str,
    pub artist: &'a str,
    pub id: &'a str,
    pub safe_title: &'a str,
    pub pool_name: &'a str,
    pub page_number: &'a str,
    pub ext: &'a str,
}

fn forbidden(c: char) -> bool {
    c.is_control() || matches!(c, '<' | '>' | ':' | '"' | '|' | '?' | '*')
}

fn sanitize(value: &str) -> String {
    let mut safe: String = value
        .trim_end_matches(['.', ' '])
        .chars()
        .map(|c| {
            if forbidden(c) || c.is_whitespace() || matches!(c, '/' | '\\' | '{' | '}') {
                '_'
            } else {
                c
            }
        })
        .collect();
    if safe.is_empty() || safe == "." || safe == ".." {
        safe = "_".into();
    }
    let stem = safe
        .split('.')
        .next()
        .unwrap_or("")
        .trim_end()
        .to_uppercase();
    let numbered = ["COM", "LPT"].iter().any(|prefix| {
        stem.strip_prefix(prefix).is_some_and(|tail| {
            ["1", "2", "3", "4", "5", "6", "7", "8", "9", "¹", "²", "³"].contains(&tail)
        })
    });
    if numbered || ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"].contains(&stem.as_str()) {
        safe.insert(0, '_');
    }
    safe
}

impl FilenameTemplate {
    pub fn parse(template: &str) -> Result<Self, TemplateError> {
        let portable = template.replace('\\', "/");
        if portable.is_empty() || portable.len() > 1024 {
            return Err(TemplateError("empty or overlong path".into()));
        }
        for segment in portable.split('/') {
            if segment.is_empty() || matches!(segment, "." | "..") || segment.ends_with(['.', ' '])
            {
                return Err(TemplateError(
                    "root, empty, parent or trailing-dot segment".into(),
                ));
            }
        }
        let mut rest = portable.as_str();
        while !rest.is_empty() {
            let (index, ch) = match rest.char_indices().find(|(_, c)| *c == '{' || *c == '}') {
                Some(found) => found,
                None => {
                    if rest.chars().any(forbidden) {
                        return Err(TemplateError("invalid literal".into()));
                    }
                    break;
                }
            };
            if rest[..index].chars().any(forbidden) || ch == '}' {
                return Err(TemplateError("invalid literal or unmatched brace".into()));
            }
            let after = &rest[index + 1..];
            let end = after
                .find('}')
                .ok_or_else(|| TemplateError("unclosed token".into()))?;
            if ![
                "source",
                "artist",
                "id",
                "safeTitle",
                "pool_name",
                "page_number",
                "ext",
            ]
            .contains(&&after[..end])
            {
                return Err(TemplateError("unknown token".into()));
            }
            rest = &after[end + 1..];
        }
        Ok(Self(portable))
    }
    /// Returns a portable relative path, not authorization to write files.
    pub fn render(&self, values: &TemplateValues<'_>) -> Result<String, TemplateError> {
        // Scan only the parsed template, never reparse injected token values.
        let mut rest = self.0.as_str();
        let mut output = String::new();
        while let Some(start) = rest.find('{') {
            output.push_str(&rest[..start]);
            let after = &rest[start + 1..];
            let end = after.find('}').expect("validated template token");
            let value = match &after[..end] {
                "source" => values.source,
                "artist" => values.artist,
                "id" => values.id,
                "safeTitle" => values.safe_title,
                "pool_name" => values.pool_name,
                "page_number" => values.page_number,
                "ext" => values.ext,
                _ => unreachable!("validated template token"),
            };
            output.push_str(&sanitize(value));
            rest = &after[end + 1..];
        }
        output.push_str(rest);
        let segments: Vec<_> = output.split('/').map(sanitize).collect();
        if segments
            .iter()
            .any(|s| s.len() > 255 || s.encode_utf16().count() > 255)
        {
            return Err(TemplateError(
                "expanded segment exceeds 255 bytes or UTF-16 units".into(),
            ));
        }
        let output = segments.join("/");
        if output.len() > 1024 {
            return Err(TemplateError("expanded path exceeds 1024 bytes".into()));
        }
        Ok(output)
    }
}
