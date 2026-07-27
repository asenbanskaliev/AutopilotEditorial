using System.Text.Json;

namespace BookStudio.Mcp.Production;

/// <summary>Canonical schemas and deterministic preflight profile for book-production.</summary>
public static class BookProductionSchemas
{
    public const string PrepareInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-production/release-prepare-input",
          "type":"object",
          "required":["projectId","payload"],
          "properties":{
            "projectId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
            "payload":{
              "type":"object",
              "required":["releaseId","expectedVersion","title","language","sources"],
              "properties":{
                "releaseId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
                "expectedVersion":{"type":"integer","minimum":1},
                "title":{"type":"string","minLength":1,"maxLength":200},
                "language":{"type":"string","minLength":2,"maxLength":32},
                "sources":{
                  "type":"array","minItems":1,"maxItems":50,
                  "items":{
                    "type":"object","required":["role","artifactId","version"],
                    "properties":{
                      "role":{"type":"string","enum":["manuscript","cover","metadata","interior-pdf","epub","supplemental"]},
                      "artifactId":{"type":"string","pattern":"^[a-z0-9][a-z0-9._-]{0,127}$"},
                      "version":{"type":"integer","minimum":1}
                    },
                    "additionalProperties":false
                  }
                }
              },
              "additionalProperties":false
            }
          },
          "additionalProperties":false
        }
        """;

    public const string PreflightInputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-production/preflight-input",
          "type":"object",
          "required":["projectId","payload"],
          "properties":{
            "projectId":{"type":"string","pattern":"^[a-z0-9][a-z0-9-]{0,63}$"},
            "payload":{
              "type":"object","required":["releaseArtifactId","version"],
              "properties":{
                "releaseArtifactId":{"type":"string","pattern":"^[a-z0-9][a-z0-9._-]{0,127}$"},
                "version":{"type":"integer","minimum":1},
                "profile":{"type":"string","enum":["release-basic"],"default":"release-basic"}
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
          "$id":"book://schemas/book-production/artifact-reference",
          "type":"object",
          "required":["artifactId","version","sha256","length","mediaType","uri"],
          "properties":{
            "artifactId":{"type":"string"},"version":{"type":"integer","minimum":1},
            "sha256":{"type":"string","pattern":"^[a-f0-9]{64}$"},
            "length":{"type":"integer","minimum":0},"mediaType":{"type":"string"},
            "uri":{"type":"string","format":"uri"}
          },
          "additionalProperties":false
        }
        """;

    public const string PrepareOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-production/release-prepare-output",
          "type":"object","required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","maxLength":64},
            "artifactRefs":{"type":"array","maxItems":1,"items":{"$ref":"book://schemas/book-production/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20},
            "data":{"type":"object"},"error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string PreflightOutputJson =
        """
        {
          "$schema":"https://json-schema.org/draft/2020-12/schema",
          "$id":"book://schemas/book-production/preflight-output",
          "type":"object","required":["resultType","operationId","artifactRefs","warnings","data"],
          "properties":{
            "resultType":{"type":"string","enum":["complete","failed"]},
            "operationId":{"type":"string","maxLength":64},
            "artifactRefs":{"type":"array","maxItems":1,"items":{"$ref":"book://schemas/book-production/artifact-reference"}},
            "warnings":{"type":"array","maxItems":20},
            "data":{
              "type":"object","properties":{
                "profile":{"type":"string"},"decision":{"type":"string","enum":["PASS","BLOCKED"]},
                "checks":{"type":"array","maxItems":20},"blockingReasons":{"type":"array","maxItems":20}
              }
            },
            "error":{"type":["object","null"]}
          },
          "additionalProperties":false
        }
        """;

    public const string ToolStructuredResultJson =
        """
        {"$schema":"https://json-schema.org/draft/2020-12/schema","$id":"book://schemas/book-production/tool-result",
         "type":"object","required":["content","structuredContent","isError"],
         "properties":{"content":{"type":"array"},"structuredContent":{"type":"object"},"isError":{"type":"boolean"}},
         "additionalProperties":false}
        """;

    public const string ReleaseBasicProfileJson =
        """
        {
          "id":"release-basic",
          "description":"Deterministic preflight for immutable BookStudio release manifests.",
          "checks":["release.schema_version","release.project_scope","release.manuscript_present","release.no_duplicate_sources","release.sources_available","release.sources_integrity","release.role_media_compatibility"],
          "compatibleMediaTypes":{
            "manuscript":["text/markdown","text/plain"],
            "cover":["image/png","image/jpeg","image/svg+xml"],
            "metadata":["application/json"],
            "interior-pdf":["application/pdf"],
            "epub":["application/epub+zip"],
            "supplemental":["*"]
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
            ["book://production/profiles/release-basic"] = Compact(ReleaseBasicProfileJson),
            ["book://schemas/book-production/artifact-reference"] = Compact(ArtifactReferenceJson),
            ["book://schemas/book-production/preflight-input"] = Compact(PreflightInputJson),
            ["book://schemas/book-production/preflight-output"] = Compact(PreflightOutputJson),
            ["book://schemas/book-production/release-prepare-input"] = Compact(PrepareInputJson),
            ["book://schemas/book-production/release-prepare-output"] = Compact(PrepareOutputJson),
            ["book://schemas/book-production/tool-result"] = Compact(ToolStructuredResultJson),
        };

    private static string Compact(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }

    // MCP definition contract tokens: "inputSchema", "outputSchema", "structuredContent",
    // "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint",
    // "taskSupport", "forbidden". PrepareTool and PreflightTool have distinct annotations.
}
