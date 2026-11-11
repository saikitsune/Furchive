CREATE TABLE saved_searches (
    id TEXT PRIMARY KEY NOT NULL CHECK(length(id) > 0),
    name TEXT NOT NULL CHECK(length(name) > 0),
    query_json TEXT NOT NULL CHECK(json_valid(query_json))
);
CREATE TABLE pinned_pools (
    source TEXT NOT NULL CHECK(length(source) > 0),
    pool_id TEXT NOT NULL CHECK(length(pool_id) > 0),
    PRIMARY KEY(source, pool_id)
);
