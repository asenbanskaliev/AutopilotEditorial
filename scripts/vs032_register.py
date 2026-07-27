from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

architecture = ROOT / "docs/architecture/architecture-policy.json"
text = architecture.read_text(encoding="utf-8")
entry = '    {"name":"BookStudio.Tests.OpenCodeSseReconciliation","projectPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj","layer":"integration-test","outputAssemblyPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/bin/Release/net10.0/BookStudio.Tests.OpenCodeSseReconciliation.dll","allowedProjectReferences":["../../src/BookStudio.Application/BookStudio.Application.csproj","../../src/BookStudio.OpenCode/BookStudio.OpenCode.csproj"],"allowedBookStudioAssemblyReferences":["BookStudio.Application","BookStudio.OpenCode"],"packagePolicy":"none","agentsPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/AGENTS.md","forbiddenNamespacePrefixes":[]}'
if '"name":"BookStudio.Tests.OpenCodeSseReconciliation"' not in text:
    anchor = '    {"name":"BookStudio.Tests.OpenCodeSessionLifecycle"'
    start = text.index(anchor)
    end = text.index("\n  ]", start)
    text = text[:end] + ",\n" + entry + text[end:]
    architecture.write_text(text, encoding="utf-8")

providers = ROOT / "config/ci/providers.json"
text = providers.read_text(encoding="utf-8")
contract = '    {"id":"dotnet.opencode-sse-reconciliation-integration","capability":"integration","localEquivalentAllowed":true,"command":["dotnet","run","--project","tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj","--no-build","-c","Release"]},\n'
if "dotnet.opencode-sse-reconciliation-integration" not in text:
    text = text.replace('    {"id":"dotnet.build-test"', contract + '    {"id":"dotnet.build-test"', 1)
    providers.write_text(text, encoding="utf-8")

lifecycle = ROOT / "src/BookStudio.OpenCode/OpenCodeSessionLifecycleClient.cs"
text = lifecycle.read_text(encoding="utf-8")
old = "        return ParseStatuses(response.Payload);"
new = """        try
        {
            return OpenCodeSessionStatusParser.ParseSnapshot(
                response.Payload,
                _options.MaximumStatusEntries);
        }
        catch (OpenCodeSessionStatusPayloadException)
        {
            throw new OpenCodeSessionLifecycleException(
                OpenCodeSessionErrorCodes.StatusPayloadInvalid);
        }"""
if old in text:
    lifecycle.write_text(text.replace(old, new, 1), encoding="utf-8")
elif "OpenCodeSessionStatusParser.ParseSnapshot" not in text:
    raise SystemExit("Lifecycle status parser anchor missing")

workflow = ROOT / ".github/workflows/02-dotnet-ci.yml"
workflow.write_text("""name: .NET CI

on:
  pull_request:
    paths:
      - "src/**/*.cs"
      - "src/**/*.csproj"
      - "src/**/*.sql"
      - "src/**/*.html"
      - "src/**/*.css"
      - "src/**/*.js"
      - "tests/**/*.cs"
      - "tests/**/*.csproj"
      - "tests/**/*.json"
      - "*.sln"
      - "*.slnx"
      - "Directory.Build.*"
      - "Directory.Packages.*"
      - "global.json"

permissions:
  contents: read

jobs:
  build-test:
    runs-on: ubuntu-latest
    timeout-minutes: 20
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v5
        with:
          global-json-file: global.json
      - uses: actions/setup-python@v5
        with:
          python-version: "3.12"
      - name: Show SDK
        run: dotnet --info
      - name: Restore solution
        run: dotnet restore BookStudio.slnx
      - name: Build solution
        run: dotnet build BookStudio.slnx --no-restore -c Release
      - name: Run architecture fitness
        run: dotnet run --project tests/BookStudio.Tests.Architecture/BookStudio.Tests.Architecture.csproj --no-build -c Release
      - name: Run SQLite integration journey
        run: dotnet run --project tests/BookStudio.Tests.Integration/BookStudio.Tests.Integration.csproj --no-build -c Release
      - name: Run artifact-store integration journey
        run: dotnet run --project tests/BookStudio.Tests.Artifacts/BookStudio.Tests.Artifacts.csproj --no-build -c Release
      - name: Run Outbox integration journey
        run: dotnet run --project tests/BookStudio.Tests.Outbox/BookStudio.Tests.Outbox.csproj --no-build -c Release
      - name: Run API and shell integration journey
        run: dotnet run --project tests/BookStudio.Tests.Api/BookStudio.Tests.Api.csproj --no-build -c Release
      - name: Run OpenTelemetry integration journey
        run: dotnet run --project tests/BookStudio.Tests.Observability/BookStudio.Tests.Observability.csproj --no-build -c Release
      - name: Run MCP initialize integration journey
        run: dotnet run --project tests/BookStudio.Tests.McpInitialize/BookStudio.Tests.McpInitialize.csproj --no-build -c Release
      - name: Run book-core integration journey
        run: dotnet run --project tests/BookStudio.Tests.BookCore/BookStudio.Tests.BookCore.csproj --no-build -c Release
      - name: Run book-authoring integration journey
        run: dotnet run --project tests/BookStudio.Tests.BookAuthoring/BookStudio.Tests.BookAuthoring.csproj --no-build -c Release
      - name: Run book-quality integration journey
        run: dotnet run --project tests/BookStudio.Tests.BookQuality/BookStudio.Tests.BookQuality.csproj --no-build -c Release
      - name: Run book-production integration journey
        run: dotnet run --project tests/BookStudio.Tests.BookProduction/BookStudio.Tests.BookProduction.csproj --no-build -c Release
      - name: Run book-ops integration journey
        run: dotnet run --project tests/BookStudio.Tests.BookOps/BookStudio.Tests.BookOps.csproj --no-build -c Release
      - name: Run prompts-resources integration journey
        run: dotnet run --project tests/BookStudio.Tests.PromptsResources/BookStudio.Tests.PromptsResources.csproj --no-build -c Release
      - name: Run MCP conformance integration journey
        run: dotnet run --project tests/BookStudio.Tests.McpConformance/BookStudio.Tests.McpConformance.csproj --no-build -c Release
      - name: Run MCP security sandbox integration journey
        run: dotnet run --project tests/BookStudio.Tests.McpSecuritySandbox/BookStudio.Tests.McpSecuritySandbox.csproj --no-build -c Release
      - name: Run OpenCode compatibility integration journey
        run: dotnet run --project tests/BookStudio.Tests.OpenCodeCompatibility/BookStudio.Tests.OpenCodeCompatibility.csproj --no-build -c Release
      - name: Run OpenCode session lifecycle integration journey
        run: dotnet run --project tests/BookStudio.Tests.OpenCodeSessionLifecycle/BookStudio.Tests.OpenCodeSessionLifecycle.csproj --no-build -c Release
      - name: Run OpenCode SSE reconciliation integration journey
        run: dotnet run --project tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj --no-build -c Release
      - name: Generate normalized build evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.solution-baseline --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-solution-baseline.json -- dotnet build BookStudio.slnx --no-restore -c Release
      - name: Generate normalized SQLite evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.sqlite-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-sqlite-integration.json -- dotnet run --project tests/BookStudio.Tests.Integration/BookStudio.Tests.Integration.csproj --no-build -c Release
      - name: Generate normalized artifact-store evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.artifact-store-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-artifact-store-integration.json -- dotnet run --project tests/BookStudio.Tests.Artifacts/BookStudio.Tests.Artifacts.csproj --no-build -c Release
      - name: Generate normalized Outbox evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.outbox-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-outbox-integration.json -- dotnet run --project tests/BookStudio.Tests.Outbox/BookStudio.Tests.Outbox.csproj --no-build -c Release
      - name: Generate normalized API health evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.api-health-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-api-health-integration.json -- dotnet run --project tests/BookStudio.Tests.Api/BookStudio.Tests.Api.csproj --no-build -c Release
      - name: Generate normalized Control Center shell evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.control-center-shell-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-control-center-shell-integration.json -- dotnet run --project tests/BookStudio.Tests.Api/BookStudio.Tests.Api.csproj --no-build -c Release
      - name: Generate normalized OpenTelemetry evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.opentelemetry-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-opentelemetry-integration.json -- dotnet run --project tests/BookStudio.Tests.Observability/BookStudio.Tests.Observability.csproj --no-build -c Release
      - name: Generate normalized MCP initialize evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.mcp-initialize-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-mcp-initialize-integration.json -- dotnet run --project tests/BookStudio.Tests.McpInitialize/BookStudio.Tests.McpInitialize.csproj --no-build -c Release
      - name: Generate normalized book-core evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.book-core-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-book-core-integration.json -- dotnet run --project tests/BookStudio.Tests.BookCore/BookStudio.Tests.BookCore.csproj --no-build -c Release
      - name: Generate normalized book-authoring evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.book-authoring-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-book-authoring-integration.json -- dotnet run --project tests/BookStudio.Tests.BookAuthoring/BookStudio.Tests.BookAuthoring.csproj --no-build -c Release
      - name: Generate normalized book-quality evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.book-quality-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-book-quality-integration.json -- dotnet run --project tests/BookStudio.Tests.BookQuality/BookStudio.Tests.BookQuality.csproj --no-build -c Release
      - name: Generate normalized book-production evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.book-production-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-book-production-integration.json -- dotnet run --project tests/BookStudio.Tests.BookProduction/BookStudio.Tests.BookProduction.csproj --no-build -c Release
      - name: Generate normalized book-ops evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.book-ops-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-book-ops-integration.json -- dotnet run --project tests/BookStudio.Tests.BookOps/BookStudio.Tests.BookOps.csproj --no-build -c Release
      - name: Generate normalized prompts-resources evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.prompts-resources-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-prompts-resources-integration.json -- dotnet run --project tests/BookStudio.Tests.PromptsResources/BookStudio.Tests.PromptsResources.csproj --no-build -c Release
      - name: Generate normalized MCP conformance evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.mcp-conformance-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-mcp-conformance-integration.json -- dotnet run --project tests/BookStudio.Tests.McpConformance/BookStudio.Tests.McpConformance.csproj --no-build -c Release
      - name: Generate normalized MCP security sandbox evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.mcp-security-sandbox-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-mcp-security-sandbox-integration.json -- dotnet run --project tests/BookStudio.Tests.McpSecuritySandbox/BookStudio.Tests.McpSecuritySandbox.csproj --no-build -c Release
      - name: Generate normalized OpenCode compatibility evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.opencode-compatibility-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-opencode-compatibility-integration.json -- dotnet run --project tests/BookStudio.Tests.OpenCodeCompatibility/BookStudio.Tests.OpenCodeCompatibility.csproj --no-build -c Release
      - name: Generate normalized OpenCode session lifecycle evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.opencode-session-lifecycle-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-opencode-session-lifecycle-integration.json -- dotnet run --project tests/BookStudio.Tests.OpenCodeSessionLifecycle/BookStudio.Tests.OpenCodeSessionLifecycle.csproj --no-build -c Release
      - name: Generate normalized OpenCode SSE reconciliation evidence
        if: always()
        run: python scripts/run_local_validation.py --provider local-evidence-default --contract dotnet.opencode-sse-reconciliation-integration --source-sha "${{ github.sha }}" --output artifacts/ci/dotnet-opencode-sse-reconciliation-integration.json -- dotnet run --project tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj --no-build -c Release
      - name: Upload .NET evidence
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: dotnet-ci-evidence
          path: artifacts/ci/*.json
          if-no-files-found: error
""", encoding="utf-8")

legacy = ROOT / ".github/workflows/99-vs032-register.yml"
if legacy.exists():
    legacy.unlink()

Path(__file__).unlink()
