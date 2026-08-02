# VS-125 — RetroSpec

Status: IMPLEMENTED — pending same-head CI.

VS-125 introduces `CommercialImageVerificationAuthority`, a provider decorator that requires moderation and independent rights clearance for the exact generated digest before delegating accepted output to the existing durable image evidence pipeline.

Retained boundaries:
- editorial approval and publication readiness remain authoritative;
- commercial providers, moderation vendors and rights registries are replaceable adapters;
- restart reuse requires exact asset, digest, provider and request identity;
- aggregate provider and verification cost remains under one configured ceiling;
- automatic retries remain bounded by the existing image repair policy;
- absence or contradiction of evidence fails closed.