# MCP Adapter Instructions

## Allowed

- MCP initialize, tools, resources, prompts and protocol error mapping.
- Input/output schema validation.
- Translation to Application use cases.
- Transport lifecycle, cancellation and progress.
- References to Application and Infrastructure for composition.

## Forbidden

- Editorial domain rules.
- Durable workflow ownership or canonical memory.
- Direct mutation of storage outside Application use cases.
- Exposing internal capabilities as public tools without contract approval.
- Writing logs to stdout in stdio mode.

Every public MCP capability requires conformance and contract tests.
