# VS-124 — Deployment-grade image provider and rights provenance authority

## Intent
Connect image generation to the durable no-command product through a typed provider boundary that persists exact image bytes and fail-closed provenance, rights and accessibility evidence.

## Invariants
- One provider owns one image request.
- Quote and charged cost must match currency and remain within the configured ceiling.
- Image and evidence writes are atomic and confined to the workspace.
- Rights evidence must include an allowed license, reference, holder and sufficient territory.
- Provenance includes provider, model, provider request, prompt digest and artifact digest.
- Accessibility evidence includes non-empty alt text.
- Restart replay reuses only an exact digest match.
- Automatic repair never exceeds the configured ceiling.
- Missing or invalid rights, provenance, accessibility, media type or cost fails closed.

## Acceptance
The executable integration smoke creates a real SVG, verifies exact bytes and evidence, restarts and reuses the same digest, rejects invalid rights and excessive cost, and proves bounded repair.