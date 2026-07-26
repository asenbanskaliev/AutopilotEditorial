# VS-020 — Dual Red Evidence

## RED-I

`Governance Gates` run `30216888322`, job `89832550025`, failed in the MCP initialize contract tests after plan integrity, completion policy and CI-provider validation passed.

Missing behavior:

- no MCP protocol-version catalog;
- no JSON-RPC envelopes or errors;
- no initialize request/response contracts;
- no lifecycle state machine;
- no stdio JSON-RPC server;
- no subprocess journey;
- no normalized MCP initialize CI contract.

## RED-E

The current `BookStudio.Mcp` process writes a human-readable baseline banner to stdout and exits. It cannot perform initialize negotiation, receive `notifications/initialized`, answer ping, map malformed JSON to protocol errors or close after an MCP session.

## Confirmation

Existing OpenTelemetry, API, persistence and artifact journeys provide no MCP conformance evidence. The RED failure is scoped to VS-020.
