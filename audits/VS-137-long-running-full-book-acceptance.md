# VS-137 Independent Acceptance Audit

## Scope reviewed

The acceptance harness exercises persisted checkpoints, independent literary evaluation, bounded revision, restart reconstruction, duplicate and loss detection, quality trend validation, deterministic KDP generation and secret-leak scanning.

## Independence controls

- Writer identity: `writer-agent`.
- Reviser identity: `editor-reviser`.
- Evaluator identity: `independent-reviewer`.
- The production quality gate rejects identity collisions.

## Failure conditions

The run fails on missing or duplicate chapters, unexpected assessment counts, lack of quality improvement, non-PASS final decisions, changed package hashes, changed manifest hashes, missing package files or evidence containing the canary secret.

## Evidence

CI persists `artifacts/vs137/long-running-full-book.json`. The artifact contains chapter totals, restart count, per-chapter quality trends, package file hashes, final ZIP and manifest hashes, and secret scan status.

## Merge decision

Merge is permitted only when the VS-137 workflow and every inherited exact-head check complete successfully and no review thread remains open.
