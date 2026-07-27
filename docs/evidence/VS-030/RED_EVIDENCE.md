# VS-030 — RED Evidence

## RED-I

The current `BookStudio.OpenCode` project contains only an assembly marker. The following required contracts do not exist:

- Application compatibility port and immutable result models;
- stable OpenCode feature catalog;
- endpoint/auth/bound options;
- HTTP health client;
- bounded OpenAPI inspector;
- compatibility probe implementation;
- integration journey and contractual local server;
- architecture and CI registration.

Governance contracts intentionally fail until those components are implemented.

## RED-E

There is no executable journey proving:

```text
health
→ version
→ OpenAPI feature detection
→ auth
→ byte/time/cancellation bounds
→ no side effects
→ safe degraded/unavailable reports
```

No current test can distinguish a healthy OpenCode server from a compatible one.

## Expected RED gate

- Plan Integrity: PASS.
- Governance: FAIL because VS-030 contracts are absent.
- Existing .NET journeys may remain green because no product code has changed yet.

No RED requirement may be removed to obtain GREEN. Test changes require a documented TCR.
