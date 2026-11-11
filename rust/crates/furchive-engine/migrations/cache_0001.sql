CREATE TABLE posts (
    source TEXT NOT NULL CHECK(length(source) > 0),
    post_id TEXT NOT NULL CHECK(length(post_id) > 0),
    data_json TEXT NOT NULL CHECK(json_valid(data_json)),
    PRIMARY KEY(source, post_id)
);
CREATE TABLE pools (
    source TEXT NOT NULL CHECK(length(source) > 0),
    pool_id TEXT NOT NULL CHECK(length(pool_id) > 0),
    data_json TEXT NOT NULL CHECK(json_valid(data_json)),
    PRIMARY KEY(source, pool_id)
);
-- Intentionally no post foreign key: uncached/unavailable members keep position.
CREATE TABLE pool_posts (
    source TEXT NOT NULL,
    pool_id TEXT NOT NULL,
    post_id TEXT NOT NULL CHECK(length(post_id) > 0),
    position INTEGER NOT NULL CHECK(position >= 0 AND position <= 4294967295),
    PRIMARY KEY(source, pool_id, post_id),
    UNIQUE(source, pool_id, position),
    FOREIGN KEY(source, pool_id) REFERENCES pools(source, pool_id) ON DELETE CASCADE
);
CREATE INDEX pool_posts_by_post ON pool_posts(source, post_id);
