using System.Text.Json;
using BookStudio.Application.Operations;

namespace BookStudio.Mcp.Ops;

/// <summary>Canonical schemas and capability resource for book-ops.</summary>
public static class BookOpsSchemas
{
    public const string EmptyInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-ops/empty-input",
          "type":"object",
          "properties":{},
          "additionalProperties":false
        }
        """;

    public const string StatusOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-ops/status-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":0},
            "warnings":{"type":"array","maxItems":20},
            "data":{
              "type":"object",
              "properties":{
                "status":{"type":"string","enum":["ready","notReady","degraded"]},
                "probeCount":{"type":"integer","minimum":1},
                "readyProbeCount":{"type":"integer","minimum":0},
                "autopilotAvailability":{"type":"string","enum":["unavailable"]},
                "unreadyProbes":{"type":"array","maxItems":20,"items":{"type":"string"}},
                "reservedComponents":{"type":"array","maxItems":20,"items":{"type":"string"}}
              }
            },
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string DiagnosticsOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-ops/diagnostics-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":0},
            "warnings":{"type":"array","maxItems":20},
            "data":{
              "type":"object",
              "properties":{
                "status":{"type":"string","enum":["ready","notReady","degraded"]},
                "checks":{"type":"array","maxItems":20,"items":{"type":"object"}},
                "capabilities":{"type":"array","maxItems":50,"items":{"type":"object"}},
                "recommendations":{"type":"array","maxItems":20,"items":{"type":"string"}}
              }
            },
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string ToolStructuredResultJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-ops/tool-result",
          "type":"object",
          "required":["content","structuredContent","isError"],
          "properties":{
            "content":{"type":"array"},
            "structuredContent":{"type":"object"},
            "isError":{"type":"boolean"}
          },
          "additionalProperties":false
        }
        """;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string CapabilityResourceJson { get; } = JsonSerializer.Serialize(
        new
        {
            schemaVersion = "1.0.0",
            capabilities = OperationsCapabilityCatalog.All
                .OrderBy(capability => capability.Id, StringComparer.Ordinal)
                .ToArray(),
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static IReadOnlyDictionary<string, string> ResourceDocuments { get; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["book://ops/capabilities"] = Compact(CapabilityResourceJson),
            ["book://schemas/book-ops/diagnostics-output"] = Compact(DiagnosticsOutputJson),
            ["book://schemas/book-ops/empty-input"] = Compact(EmptyInputJson),
            ["book://schemas/book-ops/status-output"] = Compact(StatusOutputJson),
            ["book://schemas/book-ops/tool-result"] = Compact(ToolStructuredResultJson),
        };

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    // MCP definition contract tokens: "inputSchema", "outputSchema", "structuredContent",
    // "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint",
    // "taskSupport", "forbidden". StatusTool and DiagnosticsTool are distinct read-only tools.
}
