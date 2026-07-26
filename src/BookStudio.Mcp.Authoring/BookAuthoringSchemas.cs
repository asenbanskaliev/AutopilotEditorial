using System.Text.Json;

namespace BookStudio.Mcp.Authoring;

/// <summary>Canonical schemas for the bounded book-authoring surface.</summary>
public static class BookAuthoringSchemas
{
    public const string DraftRegisterInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-authoring/draft-register-input",
          "type":"object",
          "required":["projectId","payload"],
          "properties":{
            "projectId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
            "payload":{
              "type":"object",
              "required":["artifactId","expectedVersion","mediaType","content"],
              "properties":{
                "artifactId":{"type":"string","pattern":"^[a-z0-9][a-z0-9._-]{0,127}$"},
                "expectedVersion":{"type":"integer","minimum":1},
                "mediaType":{"type":"string","enum":["text/markdown","text/plain"]},
                "content":{"type":"string","minLength":1,"maxLength":524288}
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string DraftValidateInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-authoring/draft-validate-input",
          "type":"object",
          "required":["projectId","payload"],
          "properties":{
            "projectId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
            "payload":{
              "type":"object",
              "required":["artifactId","version"],
              "properties":{
                "artifactId":{"type":"string","pattern":"^[a-z0-9][a-z0-9._-]{0,127}$"},
                "version":{"type":"integer","minimum":1},
                "maximumLineLength":{"type":"integer","minimum":40,"maximum":240,"default":120}
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string DraftReferenceJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-authoring/draft-reference",
          "type":"object",
          "required":["artifactId","version","sha256","length","mediaType","uri"],
          "properties":{
            "artifactId":{"type":"string"},
            "version":{"type":"integer","minimum":1},
            "sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},
            "length":{"type":"integer","minimum":0},
            "mediaType":{"type":"string"},
            "uri":{"type":"string","format":"uri"}
          },
          "additionalProperties":false
        }
        """;

    public const string DraftRegisterOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-authoring/draft-register-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":2,"items":{"$ref":"book://schemas/book-authoring/draft-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"object"}},
            "data":{"type":"object"},
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string DraftValidateOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-authoring/draft-validate-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":2,"items":{"$ref":"book://schemas/book-authoring/draft-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"object"}},
            "data":{
              "type":"object",
              "properties":{
                "artifact":{"$ref":"book://schemas/book-authoring/draft-reference"},
                "metrics":{"type":"object"},
                "isValid":{"type":"boolean"}
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
          "$id":"book://schemas/book-authoring/tool-result",
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

    public static IReadOnlyDictionary<string, string> ResourceSchemas { get; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["book://schemas/book-authoring/draft-reference"] = Compact(DraftReferenceJson),
            ["book://schemas/book-authoring/draft-register-input"] = Compact(DraftRegisterInputJson),
            ["book://schemas/book-authoring/draft-register-output"] = Compact(DraftRegisterOutputJson),
            ["book://schemas/book-authoring/draft-validate-input"] = Compact(DraftValidateInputJson),
            ["book://schemas/book-authoring/draft-validate-output"] = Compact(DraftValidateOutputJson),
            ["book://schemas/book-authoring/tool-result"] = Compact(ToolStructuredResultJson),
        };

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    // MCP definition contract tokens: "inputSchema", "outputSchema", "structuredContent",
    // "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint",
    // "taskSupport", "forbidden". RegisterTool and ValidateTool have distinct annotations.
}
