using System.Text.Json;

namespace BookStudio.Mcp.Quality;

/// <summary>Canonical schemas and deterministic profile resources for book-quality.</summary>
public static class BookQualitySchemas
{
    public const string AuditInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-quality/audit-input",
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
                "minimumWords":{"type":"integer","minimum":1,"maximum":50000,"default":1},
                "maximumSentenceWords":{"type":"integer","minimum":10,"maximum":300,"default":60}
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string GateInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-quality/gate-input",
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
                "profile":{"type":"string","enum":["draft-basic"],"default":"draft-basic"},
                "minimumWords":{"type":"integer","minimum":1,"maximum":50000,"default":1},
                "maximumWarnings":{"type":"integer","minimum":0,"maximum":100,"default":3},
                "blockOnPlaceholders":{"type":"boolean","default":true}
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string ArtifactReferenceJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-quality/artifact-reference",
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

    public const string AuditOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-quality/audit-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":1,"items":{"$ref":"book://schemas/book-quality/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"object"}},
            "data":{
              "type":"object",
              "properties":{
                "artifact":{"$ref":"book://schemas/book-quality/artifact-reference"},
                "metrics":{"type":"object"},
                "checks":{"type":"array","maxItems":20,"items":{"type":"object"}},
                "isPassing":{"type":"boolean"}
              }
            },
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string GateOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-quality/gate-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":1,"items":{"$ref":"book://schemas/book-quality/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"object"}},
            "data":{
              "type":"object",
              "properties":{
                "profile":{"type":"string"},
                "decision":{"type":"string","enum":["PASS","BLOCKED"]},
                "blockingReasons":{"type":"array","maxItems":20,"items":{"type":"string"}},
                "audit":{"type":"object"}
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
          "$id":"book://schemas/book-quality/tool-result",
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

    public const string DraftBasicProfileJson =
        """
        {
          "id":"draft-basic",
          "description":"Deterministic pre-AI structural quality gate for immutable draft artifacts.",
          "checks":[
            "content.non_empty",
            "content.minimum_words",
            "content.no_placeholders",
            "content.no_adjacent_duplicate_paragraphs",
            "style.maximum_sentence_words",
            "structure.has_paragraphs"
          ],
          "defaults":{
            "minimumWords":1,
            "maximumSentenceWords":60,
            "maximumWarnings":3,
            "blockOnPlaceholders":true
          }
        }
        """;

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static IReadOnlyDictionary<string, string> ResourceDocuments { get; } =
        new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["book://quality/profiles/draft-basic"] = Compact(DraftBasicProfileJson),
            ["book://schemas/book-quality/artifact-reference"] = Compact(ArtifactReferenceJson),
            ["book://schemas/book-quality/audit-input"] = Compact(AuditInputJson),
            ["book://schemas/book-quality/audit-output"] = Compact(AuditOutputJson),
            ["book://schemas/book-quality/gate-input"] = Compact(GateInputJson),
            ["book://schemas/book-quality/gate-output"] = Compact(GateOutputJson),
            ["book://schemas/book-quality/tool-result"] = Compact(ToolStructuredResultJson),
        };

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    // MCP definition contract tokens: "inputSchema", "outputSchema", "structuredContent",
    // "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint",
    // "taskSupport", "forbidden". AuditTool and GateTool are distinct read-only tools.
}
