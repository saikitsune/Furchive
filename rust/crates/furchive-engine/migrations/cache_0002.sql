ALTER TABLE posts ADD COLUMN fetched_at INTEGER;
ALTER TABLE pools ADD COLUMN fetched_at INTEGER;
UPDATE posts SET fetched_at = json_extract(data_json, '$.fetched_at');
UPDATE pools SET fetched_at = json_extract(data_json, '$.fetched_at');
CREATE INDEX posts_by_fetched_at ON posts(fetched_at);
CREATE INDEX pools_by_fetched_at ON pools(fetched_at);
