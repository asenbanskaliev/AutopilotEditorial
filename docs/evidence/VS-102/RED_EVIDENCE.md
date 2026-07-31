# VS-102 RED Evidence

## RED-I

The Application layer has no provider-neutral contracts for image adapter capabilities, requests, attempts, normalized outputs, failures, usage, cancellation, retry, provider evidence, manual ingestion, or authoritative VS-101 asset registration.

## RED-E

The persistence and integration layers have no durable image-request lifecycle, adapter attempt history, provider evidence, replay receipts, bounded retry state, cancellation recovery, output-to-asset registration linkage, deterministic Outbox event, restart-safe authority checks, or cumulative journey spanning VS-100, VS-101, and VS-102.

## Expected GREEN

Implement typed adapter contracts and boundaries, exact VS-100 authority, authoritative VS-101 registration, normalized ComfyUI/local/remote/manual behavior, safe output validation, immutable digests, provenance/rights/accessibility evidence, bounded retries, cancellation, replay and concurrency protection, rollback, restart recovery, workspace isolation, append-only history, exactly-once Outbox, cumulative integration tests, governance test, and complete audit evidence.