ALTER TABLE saved_searches ADD COLUMN record_version INTEGER NOT NULL DEFAULT 1 CHECK(record_version > 0);
