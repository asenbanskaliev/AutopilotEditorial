from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

architecture = ROOT / "docs/architecture/architecture-policy.json"
text = architecture.read_text(encoding="utf-8")
entry = '    {"name":"BookStudio.Tests.AgentToolProfiles","projectPath":"tests/BookStudio.Tests.AgentToolProfiles/BookStudio.Tests.AgentToolProfiles.csproj","layer":"integration-test","outputAssemblyPath":"tests/BookStudio.Tests.AgentToolProfiles/bin/Release/net10.0/BookStudio.Tests.AgentToolProfiles.dll","allowedProjectReferences":["../../src/BookStudio.Application/BookStudio.Application.csproj","../../src/BookStudio.OpenCode/BookStudio.OpenCode.csproj"],"allowedBookStudioAssemblyReferences":["BookStudio.Application","BookStudio.OpenCode"],"packagePolicy":"none","agentsPath":"tests/BookStudio.Tests.AgentToolProfiles/AGENTS.md","forbiddenNamespacePrefixes":[]}'
if '"name":"BookStudio.Tests.AgentToolProfiles"' not in text:
    anchor = '    {"name":"BookStudio.Tests.OpenCodeSseReconciliation"'
    start = text.index(anchor)
    end = text.index("\n  ]", start)
    text = text[:end] + ",\n" + entry + text[end:]
    architecture.write_text(text, encoding="utf-8")

providers = ROOT / "config/ci/providers.json"
text = providers.read_text(encoding="utf-8")
contract = '    {"id":"dotnet.agent-tool-profiles-integration","capability":"integration","localEquivalentAllowed":true,"command":["dotnet","run","--project","tests/BookStudio.Tests.AgentToolProfiles/BookStudio.Tests.AgentToolProfiles.csproj","--no-build","-c","Release"]},\n'
if "dotnet.agent-tool-profiles-integration" not in text:
    text = text.replace('    {"id":"dotnet.build-test"', contract + '    {"id":"dotnet.build-test"', 1)
    providers.write_text(text, encoding="utf-8")
