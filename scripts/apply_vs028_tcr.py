from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
POLICY_URI = "book://security/sandbox-policy"


def replace_once(path: str, pattern: str, replacement: str) -> None:
    target = ROOT / path
    content = target.read_text(encoding="utf-8")
    updated, count = re.subn(pattern, replacement, content, count=1, flags=re.DOTALL)
    if count != 1:
        raise RuntimeError(f"Expected one VS-028 migration match in {path}, found {count}.")
    target.write_text(updated, encoding="utf-8")


replace_once(
    "tests/BookStudio.Tests.BookCore/Program.cs",
    r'''    await server\.SendRequestAsync\(3, "resources/list", new \{ \}\);.*?    Require\(resourceUris\.SequenceEqual\(resourceUris\.OrderBy\(value => value, StringComparer\.Ordinal\)\), "Resources are not ordinally sorted\."\);\n''',
    '''    string? cursor = null;
    string? lastCursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await server.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await server.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await server.ReadJsonAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString() ?? string.Empty));
        lastCursor = cursor;
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 8, "Merged resource count mismatch.");
    Require(resourceUris.Count(uri => uri.StartsWith("book://schemas/book-core/", StringComparison.Ordinal)) == 6, "Schema resource count mismatch.");
    Require(resourceUris.Contains("book://prompts/book-core/inspect-artifact/v1"), "book-core prompt resource is missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Sandbox policy resource is missing.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(value => value, StringComparer.Ordinal)), "Resources are not ordinally sorted.");
''',
)

core = ROOT / "tests/BookStudio.Tests.BookCore/Program.cs"
core_text = core.read_text(encoding="utf-8")
old = '    await server.SendRequestAsync(5, "resources/list", new { cursor = cursor + "x" });'
new = '    await server.SendRequestAsync(5, "resources/list", new { cursor = (lastCursor ?? throw new InvalidOperationException("No resource cursor was produced.")) + "x" });'
if core_text.count(old) != 1:
    raise RuntimeError("Expected one core invalid-cursor request.")
core.write_text(core_text.replace(old, new), encoding="utf-8")

replace_once(
    "tests/BookStudio.Tests.BookAuthoring/Program.cs",
    r'''    var resourceUris = new List<string>\(\);.*?    var schemaUri = resourceUris\.First\(uri => uri\.StartsWith\("book://schemas/book-authoring/", StringComparison\.Ordinal\)\);\n''',
    '''    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await server.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await server.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await server.ReadJsonAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Authoring resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Authoring resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 8, "Authoring merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Authoring resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://prompts/book-authoring/validate-draft/v1"), "Authoring prompt resource is missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Authoring sandbox policy resource is missing.");
    var schemaUri = resourceUris.First(uri => uri.StartsWith("book://schemas/book-authoring/", StringComparison.Ordinal));
''',
)

replace_once(
    "tests/BookStudio.Tests.BookQuality/Program.cs",
    r'''    var resourceUris = new List<string>\(\);.*?    Require\(resourceUris\.Contains\("book://prompts/book-quality/assess-draft/v1"\), "Quality prompt resource is missing\."\);\n''',
    '''    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await quality.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await quality.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await quality.ReadJsonAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Quality resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Quality resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 9, "Quality merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Quality resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://quality/profiles/draft-basic"), "draft-basic profile is missing.");
    Require(resourceUris.Contains("book://prompts/book-quality/assess-draft/v1"), "Quality prompt resource is missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Quality sandbox policy resource is missing.");
''',
)

replace_once(
    "tests/BookStudio.Tests.BookProduction/Program.cs",
    r'''    var resourceUris = new List<string>\(\);.*?    Require\(resourceUris\.Contains\("book://prompts/book-production/preflight-release/v1"\), "Production prompt resource missing\."\);\n''',
    '''    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await production.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await production.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await production.ReadAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Production resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Production resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 9, "Production merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Production resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://production/profiles/release-basic"), "release-basic profile missing.");
    Require(resourceUris.Contains("book://prompts/book-production/preflight-release/v1"), "Production prompt resource missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Production sandbox policy resource is missing.");
''',
)

replace_once(
    "tests/BookStudio.Tests.BookOps/Program.cs",
    r'''    var resourceUris = new List<string>\(\);.*?    Require\(resourceUris\.Contains\("book://prompts/book-ops/inspect-readiness/v1"\), "Ops prompt resource is missing\."\);\n''',
    '''    var resourceUris = new List<string>();
    string? cursor = null;
    var resourceRequestId = 1000;
    do
    {
        if (cursor is null)
        {
            await ops.SendRequestAsync(resourceRequestId++, "resources/list", new { });
        }
        else
        {
            await ops.SendRequestAsync(resourceRequestId++, "resources/list", new { cursor });
        }
        using var resourcePage = await ops.ReadJsonAsync();
        var result = resourcePage.RootElement.GetProperty("result");
        var page = result.GetProperty("resources").EnumerateArray().ToArray();
        Require(page.Length > 0, "Ops resource pagination returned an empty page.");
        resourceUris.AddRange(page.Select(item => item.GetProperty("uri").GetString()!));
        cursor = result.TryGetProperty("nextCursor", out var nextCursor)
            ? nextCursor.GetString()
            : null;
        Require(resourceRequestId <= 1020, "Ops resource pagination did not terminate.");
    }
    while (cursor is not null);
    Require(resourceUris.Count == 7, "Ops merged resource count mismatch.");
    Require(resourceUris.SequenceEqual(resourceUris.OrderBy(uri => uri, StringComparer.Ordinal)), "Ops resources are not ordinally sorted.");
    Require(resourceUris.Contains("book://ops/capabilities"), "Ops capability resource is missing.");
    Require(resourceUris.Contains("book://prompts/book-ops/inspect-readiness/v1"), "Ops prompt resource is missing.");
    Require(resourceUris.Contains("book://security/sandbox-policy"), "Ops sandbox policy resource is missing.");
''',
)

print("VS028_TCR_APPLIED")
