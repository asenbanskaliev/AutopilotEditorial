using System.Text.Json;

namespace BookStudio.Mcp.BookCore;

/// <summary>Canonical JSON schemas used by book-core tools and schema resources.</summary>
public static class BookCoreSchemas
{
    public const string ArtifactGetInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-core/artifact-get-input",
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
                "includeContent":{"type":"boolean","default":false}
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string ArtifactCompareInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-core/artifact-compare-input",
          "type":"object",
          "required":["projectId","payload"],
          "properties":{
            "projectId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
            "payload":{
              "type":"object",
              "required":["artifactId","leftVersion","rightVersion"],
              "properties":{
                "artifactId":{"type":"string","pattern":"^[a-z0-9][a-z0-9._-]{0,127}$"},
                "leftVersion":{"type":"integer","minimum":1},
                "rightVersion":{"type":"integer","minimum":1},
                "maxDifferences":{"type":"integer","minimum":1,"maximum":100,"default":20}
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
          "type":"object",
          "required":["artifactId","version","sha256","length","mediaType","createdAtUtc","uri"],
          "properties":{
            "artifactId":{"type":"string"},
            "version":{"type":"integer","minimum":1},
            "sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},
            "length":{"type":"integer","minimum":0},
            "mediaType":{"type":"string"},
            "createdAtUtc":{"type":"string","format":"date-time"},
            "uri":{"type":"string","format":"uri"}
          },
          "additionalProperties":false
        }
        """;

    public const string ArtifactGetOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-core/artifact-get-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":4,"items":{"$ref":"book://schemas/book-core/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"string","maxLength":512}},
            "data":{
              "type":"object",
              "properties":{
                "artifact":{"$ref":"book://schemas/book-core/artifact-reference"},
                "inlineText":{"type":["string","null"],"maxLength":262144},
                "contentIncluded":{"type":"boolean"}
              },
              "additionalProperties":false
            },
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string ArtifactCompareOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-core/artifact-compare-output",
          "type":"object",
          "required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","minLength":1,"maxLength":64},
            "artifactRefs":{"type":"array","maxItems":4,"items":{"$ref":"book://schemas/book-core/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20,"items":{"type":"string","maxLength":512}},
            "data":{
              "type":"object",
              "properties":{
                "left":{"$ref":"book://schemas/book-core/artifact-reference"},
                "right":{"$ref":"book://schemas/book-core/artifact-reference"},
                "identical":{"type":"boolean"},
                "summary":{"type":"object"},
                "differences":{"type":"array","maxItems":100,"items":{"type":"object"}}
              },
              "additionalProperties":false
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
          "$id":"book://schemas/book-core/tool-result",
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

    public const string ArtifactReferenceSchemaJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-core/artifact-reference",
          "type":"object",
          "required":["artifactId","version","sha256","length","mediaType","createdAtUtc","uri"],
          "properties":{
            "artifactId":{"type":"string"},
            "version":{"type":"integer","minimum":1},
            "sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},
            "length":{"type":"integer","minimum":0},
            "mediaType":{"type":"string"},
            "createdAtUtc":{"type":"string","format":"date-time"},
            "uri":{"type":"string","format":"uri"}
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
            ["book://schemas/book-core/artifact-compare-input"] = Compact(ArtifactCompareInputJson),
            ["book://schemas/book-core/artifact-compare-output"] = Compact(ArtifactCompareOutputJson),
            ["book://schemas/book-core/artifact-get-input"] = Compact(ArtifactGetInputJson),
            ["book://schemas/book-core/artifact-get-output"] = Compact(ArtifactGetOutputJson),
            ["book://schemas/book-core/artifact-reference"] = Compact(ArtifactReferenceSchemaJson),
            ["book://schemas/book-core/tool-result"] = Compact(ToolStructuredResultJson),
        };

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    // MCP definition property names remain explicit contract tokens:
    // "inputSchema", "outputSchema", "structuredContent", "readOnlyHint",
    // "destructiveHint", "idempotentHint", "openWorldHint", "taskSupport", "forbidden".
}
