"""Validate retained Cargo receipts and hash the exact owned source inventory."""
import hashlib
import json
from pathlib import Path
import re

engine = Path(__file__).resolve().parents[1]
artifacts = engine / "test-artifacts"
expected = {
    "media_contract": [
        "contributor_and_unknown_categories_roundtrip_as_original_keys",
        "media_identity_and_metadata_roundtrip_without_safe_defaults",
        "pool_order_and_identity_have_a_separate_typed_contract",
    ],
    "query_contract": [
        "include_exclude_aliases_become_individual_api_terms",
        "rating_selections_are_not_anded_and_unknown_is_never_safe",
        "rejects_chip_injection_but_preserves_upstream_metatags",
        "sort_and_artist_are_explicit_and_query_roundtrips",
    ],
    "template_contract": [
        "exact_tokens_expand_with_pool_page_context",
        "rejects_roots_traversal_unknown_tokens_and_malformed_syntax",
        "reserved_names_empty_values_unicode_and_lengths_are_portable",
        "untrusted_values_cannot_create_paths_or_recursive_tokens",
    ],
    "migration_contract": [
        "corrupted_metadata_cannot_change_a_requested_identity_or_silently_drop_rows",
        "database_failure_rolls_back_post_batch_and_pool_metadata_with_membership",
        "disk_reopen_preserves_both_stores_and_cache_clear_cannot_erase_user_state",
        "failed_upgrade_rolls_back_all_schema_steps_and_version",
        "unsupported_saved_search_record_versions_and_invalid_json_are_not_hidden",
        "upgrades_version_one_data_and_is_idempotent",
        "wrong_kind_future_schema_and_legacy_files_are_refused_without_claiming_them",
    ],
    "state_contract": [
        "pins_are_idempotent_and_source_scoped_without_cached_pools",
        "typed_searches_update_delete_and_reject_invalid_queries",
    ],
    "storage_contract": [
        "invalid_pool_snapshot_does_not_partially_replace_previous_snapshot",
        "pools_reconcile_equal_length_reorders_removals_and_many_memberships",
        "post_upsert_preserves_composite_identity_and_json_metadata",
    ],
}
log = (artifacts / "34-root-test.log").read_text()
assert log.rstrip().endswith("EXIT: 0")
assert not re.search(r"^warning:|^error:|\.\.\. (FAILED|ignored)", log, re.MULTILINE)
found = {}
for name, body in re.findall(r"Running tests/(\w+)\.rs[^\n]*\n(.*?)(?=\n\s*(?:Running |Doc-tests )|\nEXIT:|\Z)", log, re.DOTALL):
    assert name not in found
    tests = re.findall(r"^test (\w+) \.\.\. ok$", body, re.MULTILINE)
    assert sorted(tests) == sorted(expected[name]), (name, tests)
    assert f"test result: ok. {len(tests)} passed; 0 failed; 0 ignored;" in body
    found[name] = sorted(tests)
assert set(found) == set(expected)
for label in ("35-root-clippy", "36-root-fmt"):
    receipt = (artifacts / (label + ".log")).read_text()
    assert receipt.rstrip().endswith("EXIT: 0")
    assert not re.search(r"^warning:|^error:", receipt, re.MULTILINE)
files = []
for name in ("furchive-domain", "furchive-engine"):
    root = engine.parent / name
    for path in sorted(root.rglob("*")):
        if not path.is_file() or {"target", "test-artifacts", "__pycache__"}.intersection(path.relative_to(root).parts):
            continue
        files.append({"path": str(path.relative_to(engine.parents[2])), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
report = {
    "scope": ["rust/crates/furchive-domain", "rust/crates/furchive-engine"],
    "tests_passed": sum(map(len, found.values())),
    "tests_failed": 0,
    "inventory": found,
    "changed_files": files,
    "receipts": [{"path": str(path.relative_to(engine.parents[2])), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in sorted(artifacts.glob("*.log"))],
    "qualification": "Linux host execution; no independent or Windows/macOS/native media approval implied",
}
(artifacts / "evidence.json").write_text(json.dumps(report, indent=2) + "\n")
print(json.dumps(report, indent=2))
