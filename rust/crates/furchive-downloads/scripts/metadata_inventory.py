"""Intentional metadata test additions; never learn expected IDs from discovery."""
from pathlib import Path
import json
BASE = Path(__file__).resolve().parents[1]
DIRECT = [
    'metadata_process_kill_child',
    'metadata_real_process_kill_before_and_after_sidecar_publication_recovers_offline',
    'metadata_recovery_stage_never_recreates_or_rewrites_an_unrelated_file',
    'retry_metadata_on_running_media_cannot_interrupt_its_writer',
    'normalized_sidecar_follows_exact_rename_and_survives_reopen',
    'metadata_deserialization_rejects_mismatch_unknown_fields_and_oversize_before_queue',
    'persisted_metadata_status_cannot_claim_written_without_admitted_snapshot',
    'metadata_crash_windows_reconcile_without_network_or_adopting_tampered_stage',
    'metadata_collision_failure_retry_is_bounded_and_never_redownloads_media',
    'metadata_written_sidecar_tamper_is_detected_on_reopen_without_media_failure',
]
SAFETY = [
    'retry_metadata_refuses_same_directory_completed_but_still_joined_writer',
    'rejected_metadata_retry_never_recreates_missing_media_directories',
    'retry_metadata_refuses_while_same_directory_transfer_is_running',
    'snapshot_nested_identity_and_intent_unknown_fields_are_rejected',
    'sidecar_publish_refuses_substituted_staging_identity',
    'metadata_ack_database_failure_never_marks_good_media_failed',
    'matching_sidecar_is_idempotent_but_skip_and_old_jobs_never_export',
    'retry_only_verifies_same_completed_media_and_never_changes_tampered_bytes',
    'metadata_symlinks_hardlinks_and_unclaimed_stage_are_preserved_on_failure',
    'metadata_suffix_and_reserved_stage_names_cannot_mutate_database_or_engine_namespace',
    'tampered_stage_receipt_refuses_recovery_and_leaves_good_art_completed',
]
REPAIR = [
    'missing_written_retry_attempt_cap_survives_restarts',
    'missing_written_retry_kill_child',
    'missing_written_retry_real_kills_preserve_durable_stage_boundaries_offline',
    'missing_written_retry_refuses_missing_replaced_and_mutated_media',
    'removed_tampered_written_sidecar_and_failed_ack_can_retry_offline',
    'written_retry_preserves_hostile_stage_and_target_then_restores_after_removal',
]
REVIEWER = [
    'review_missing_written_sidecar_explicit_retry_restores_without_network',
    'review_snapshot_rejects_url_secrets_and_keeps_exact_limit_bound',
]
if __name__ == '__main__':
    path = BASE/'contracts/test-inventory.json'
    inventory = json.loads(path.read_text())
    additions = (['tests::metadata::'+n for n in DIRECT]
                 + ['tests::metadata::safety::'+n for n in SAFETY]
                 + ['tests::metadata_repair::'+n for n in REPAIR]
                 + ['tests::reviewer_regressions::'+n for n in REVIEWER])
    assert len(additions) == len(set(additions)) == 29
    for platform in ['linux', 'darwin', 'win32']:
        inventory[platform] = sorted(set(inventory[platform]) | set(additions))
    path.write_text(json.dumps(inventory, indent=2)+'\n')
    print({p: len(v) for p, v in inventory.items()})
