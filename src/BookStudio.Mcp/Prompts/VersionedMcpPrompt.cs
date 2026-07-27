using System.Text.Json;
using System.Text.RegularExpressions;
using BookStudio.Mcp.BookCore;

namespace BookStudio.Mcp.Prompts;

/// <summary>One immutable versioned MCP prompt and its canonical resource representation.</summary>
public sealed partial class VersionedMcpPrompt
{
    public const string ResourceMediaType =
        "application/vnd.bookstudio.prompt-template+json";
    public const int MaximumRenderedMessageLength = 4096;

    private readonly Func<IReadOnlyDictionary<string, string>, string> _renderer;

    public VersionedMcpPrompt(
        string name,
        string version,
        string resourceUri,
        string title,
        string description,
        IReadOnlyList<McpPromptArgumentDefinition> arguments,
        string messageTemplate,
        Func<IReadOnlyDictionary<string, string>, string> renderer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTemplate);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(renderer);

        if (!version.All(char.IsAsciiDigit) || version[0] == '0' || version.Length > 8)
        {
            throw new ArgumentException("Prompt version is invalid.", nameof(version));
        }
        if (!PromptNameRegex().IsMatch(name) ||
            !name.EndsWith(".v" + version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt name is invalid or does not match its version.", nameof(name));
        }
        if (!Uri.TryCreate(resourceUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "book", StringComparison.Ordinal) ||
            !resourceUri.EndsWith("/v" + version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt resource URI is invalid.", nameof(resourceUri));
        }
        ValidateNameResourceParity(name, resourceUri, version);
        if (title.Length > 128 || description.Length > 512 ||
            title.Any(char.IsControl) || description.Any(char.IsControl))
        {
            throw new ArgumentException("Prompt metadata is invalid.");
        }
        if (messageTemplate.Length > MaximumRenderedMessageLength ||
            ContainsForbiddenControl(messageTemplate))
        {
            throw new ArgumentException("Prompt message template is invalid.", nameof(messageTemplate));
        }

        var normalizedArguments = arguments
            .OrderBy(argument => argument.Name, StringComparer.Ordinal)
            .ToArray();
        ValidateArguments(normalizedArguments);

        Version = version;
        ResourceUri = resourceUri;
        MessageTemplate = messageTemplate;
        Definition = new McpPromptDefinition(
            name,
            title,
            description,
            normalizedArguments.Length == 0 ? null : normalizedArguments);
        _renderer = renderer;
        ResourceJson = BuildResourceJson();
        Resource = new McpResourceDefinition(
            resourceUri,
            name,
            title,
            "Immutable versioned MCP prompt template for " + name + ".",
            ResourceMediaType);
    }

    public string Version { get; }

    public string ResourceUri { get; }

    public string MessageTemplate { get; }

    public McpPromptDefinition Definition { get; }

    public string ResourceJson { get; }

    public McpResourceDefinition Resource { get; }

    public McpGetPromptResult Render(
        IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var message = _renderer(arguments);
        if (string.IsNullOrWhiteSpace(message) ||
            message.Length > MaximumRenderedMessageLength ||
            ContainsForbiddenControl(message))
        {
            throw new InvalidOperationException(
                "Rendered prompt message is invalid.");
        }

        return new McpGetPromptResult(
            Definition.Description,
            [
                new McpPromptMessage(
                    "user",
                    new McpTextContent("text", message)),
            ]);
    }

    private string BuildResourceJson()
    {
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1.0.0",
                promptVersion = Version,
                name = Definition.Name,
                title = Definition.Title,
                description = Definition.Description,
                arguments = Definition.Arguments ?? [],
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new
                        {
                            type = "text",
                            text = MessageTemplate,
                        },
                    },
                },
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static void ValidateNameResourceParity(
        string name,
        string resourceUri,
        string version)
    {
        var segments = new Uri(resourceUri).AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !string.Equals(segments[2], "v" + version, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt resource URI shape is invalid.", nameof(resourceUri));
        }

        var expectedContext = segments[0] switch
        {
            "book-core" => "core",
            "book-authoring" => "authoring",
            "book-quality" => "quality",
            "book-production" => "production",
            "book-ops" => "ops",
            _ => throw new ArgumentException(
                "Prompt bounded context is invalid.",
                nameof(resourceUri)),
        };
        var expectedName = $"book.{expectedContext}.{segments[1]}.v{version}";
        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Prompt name and resource URI do not describe the same versioned prompt.",
                nameof(name));
        }
    }

    private static void ValidateArguments(
        IReadOnlyList<McpPromptArgumentDefinition> arguments)
    {
        if (arguments.Count > 16 ||
            arguments.Select(argument => argument.Name)
                .Distinct(StringComparer.Ordinal).Count() != arguments.Count)
        {
            throw new ArgumentException("Prompt argument catalog is invalid.");
        }

        foreach (var argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument.Name) ||
                argument.Name.Length > 64 ||
                !argument.Name.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ||
                string.IsNullOrWhiteSpace(argument.Title) ||
                argument.Title.Length > 128 ||
                string.IsNullOrWhiteSpace(argument.Description) ||
                argument.Description.Length > 256 ||
                argument.Title.Any(char.IsControl) ||
                argument.Description.Any(char.IsControl))
            {
                throw new ArgumentException("Prompt argument metadata is invalid.");
            }
        }
    }

    private static bool ContainsForbiddenControl(string value) =>
        value.Any(character =>
            char.IsControl(character) && character is not '\r' and not '\n' and not '\t');

    [GeneratedRegex("^book\\.[a-z][a-z0-9-]{0,31}\\.[a-z0-9][a-z0-9-]{0,63}\\.v[1-9][0-9]{0,7}$", RegexOptions.CultureInvariant)]
    private static partial Regex PromptNameRegex();
}
