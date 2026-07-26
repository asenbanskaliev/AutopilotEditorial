from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "config" / "ci" / "providers.json"
EVIDENCE_SCHEMA = ROOT / "schemas" / "ci-evidence.schema.json"

ALLOWED_TYPES = {
    "github-hosted",
    "github-self-hosted",
    "circleci",
    "local-evidence",
}
ALLOWED_RESULTS = {"PASS", "FAIL", "BLOCKED"}
SECRET_REF = re.compile(r"^[A-Z][A-Z0-9_]*$")


def fail(message: str) -> None:
    raise SystemExit(f"CI provider validation FAIL: {message}")


def require(condition: bool, message: str) -> None:
    if not condition:
        fail(message)


def main() -> None:
    require(CATALOG.exists(), f"missing catalog {CATALOG}")
    require(EVIDENCE_SCHEMA.exists(), f"missing schema {EVIDENCE_SCHEMA}")

    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    providers = catalog.get("providers")
    contracts = catalog.get("contracts")
    require(catalog.get("schemaVersion") == "1.0.0", "unsupported schemaVersion")
    require(isinstance(providers, list) and providers, "providers must be a non-empty list")
    require(isinstance(contracts, list) and contracts, "contracts must be a non-empty list")

    provider_ids: set[str] = set()
    enabled_priorities: set[int] = set()
    enabled_capabilities: set[str] = set()
    enabled_local_capabilities: set[str] = set()

    for provider in providers:
        provider_id = provider.get("id")
        provider_type = provider.get("type")
        enabled = provider.get("enabled")
        priority = provider.get("priority")
        capabilities = provider.get("capabilities")
        secret_refs = provider.get("secretRefs", [])

        require(isinstance(provider_id, str) and provider_id, "provider id is required")
        require(provider_id not in provider_ids, f"duplicate provider id {provider_id}")
        provider_ids.add(provider_id)

        require(provider_type in ALLOWED_TYPES, f"unsupported provider type {provider_type}")
        require(isinstance(enabled, bool), f"enabled must be boolean for {provider_id}")
        require(isinstance(priority, int) and priority > 0, f"invalid priority for {provider_id}")
        require(
            isinstance(capabilities, list)
            and capabilities
            and all(isinstance(item, str) and item for item in capabilities),
            f"invalid capabilities for {provider_id}",
        )
        require(isinstance(secret_refs, list), f"secretRefs must be a list for {provider_id}")
        for secret_ref in secret_refs:
            require(
                isinstance(secret_ref, str) and SECRET_REF.fullmatch(secret_ref) is not None,
                f"invalid secret reference {secret_ref!r} for {provider_id}",
            )

        if enabled:
            require(
                priority not in enabled_priorities,
                f"duplicate enabled provider priority {priority}",
            )
            enabled_priorities.add(priority)
            enabled_capabilities.update(capabilities)
            if provider_type == "local-evidence":
                enabled_local_capabilities.update(capabilities)

    contract_ids: set[str] = set()
    for contract in contracts:
        contract_id = contract.get("id")
        capability = contract.get("capability")
        command = contract.get("command")
        local_allowed = contract.get("localEquivalentAllowed")

        require(isinstance(contract_id, str) and contract_id, "contract id is required")
        require(contract_id not in contract_ids, f"duplicate contract id {contract_id}")
        contract_ids.add(contract_id)
        require(
            isinstance(capability, str) and capability in enabled_capabilities,
            f"contract {contract_id} has no enabled capable provider",
        )
        require(isinstance(local_allowed, bool), f"invalid local policy for {contract_id}")
        require(
            isinstance(command, list)
            and command
            and all(isinstance(item, str) and item for item in command),
            f"invalid command for {contract_id}",
        )
        if local_allowed:
            require(
                capability in enabled_local_capabilities,
                f"contract {contract_id} allows local evidence without a capable local provider",
            )

    schema = json.loads(EVIDENCE_SCHEMA.read_text(encoding="utf-8"))
    result_values = set(schema["properties"]["result"]["enum"])
    require(result_values == ALLOWED_RESULTS, "evidence result enum must be PASS/FAIL/BLOCKED")

    print(
        "CI provider validation PASS: "
        f"{len(providers)} providers, {len(contracts)} contracts"
    )


if __name__ == "__main__":
    main()
