from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

architecture = ROOT / "docs/architecture/architecture-policy.json"
text = architecture.read_text(encoding="utf-8")
entry = '    {"name":"BookStudio.Tests.OpenCodeSseReconciliation","projectPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj","layer":"integration-test","outputAssemblyPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/bin/Release/net10.0/BookStudio.Tests.OpenCodeSseReconciliation.dll","allowedProjectReferences":["../../src/BookStudio.Application/BookStudio.Application.csproj","../../src/BookStudio.OpenCode/BookStudio.OpenCode.csproj"],"allowedBookStudioAssemblyReferences":["BookStudio.Application","BookStudio.OpenCode"],"packagePolicy":"none","agentsPath":"tests/BookStudio.Tests.OpenCodeSseReconciliation/AGENTS.md","forbiddenNamespacePrefixes":[]}'
if '"name":"BookStudio.Tests.OpenCodeSseReconciliation"' not in text:
    anchor = '    {"name":"BookStudio.Tests.OpenCodeSessionLifecycle"'
    start = text.index(anchor)
    end = text.index("\n  ]", start)
    architecture.write_text(text[:end] + ",\n" + entry + text[end:], encoding="utf-8")

providers = ROOT / "config/ci/providers.json"
text = providers.read_text(encoding="utf-8")
contract = '    {"id":"dotnet.opencode-sse-reconciliation-integration","capability":"integration","localEquivalentAllowed":true,"command":["dotnet","run","--project","tests/BookStudio.Tests.OpenCodeSseReconciliation/BookStudio.Tests.OpenCodeSseReconciliation.csproj","--no-build","-c","Release"]},\n'
if "dotnet.opencode-sse-reconciliation-integration" not in text:
    providers.write_text(
        text.replace('    {"id":"dotnet.build-test"', contract + '    {"id":"dotnet.build-test"', 1),
        encoding="utf-8",
    )

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
