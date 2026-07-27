using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

internal static class OpenCodeSessionStatusParser
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    public static IReadOnlyDictionary<string, OpenCodeSessionStatus> ParseSnapshot(
        ReadOnlyMemory<byte> payload,
        int maximumEntries = OpenCodeSessionValidation.MaximumStatusEntries)
    {
        if (maximumEntries is < 1 or > OpenCodeSessionValidation.MaximumStatusEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        try
        {
            using var document = JsonDocument.Parse(payload, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OpenCodeSessionStatusPayloadException();
            }
            EnsureUniqueProperties(root);
            var result = new SortedDictionary<string, OpenCodeSessionStatus>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (result.Count >= maximumEntries)
                {
                    throw new OpenCodeSessionStatusPayloadException();
                }
                try
                {
                    OpenCodeSessionValidation.ValidateSessionId(property.Name, "providerSessionId");
                }
                catch (ArgumentException)
                {
                    throw new OpenCodeSessionStatusPayloadException();
                }
                result.Add(property.Name, ParseStatus(property.Value));
            }
            return result;
        }
        catch (OpenCodeSessionStatusPayloadException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new OpenCodeSessionStatusPayloadException();
        }
    }

    public static OpenCodeSessionStatus ParseStatus(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new OpenCodeSessionStatusPayloadException();
        }
        EnsureUniqueProperties(value);
        if (!value.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            throw new OpenCodeSessionStatusPayloadException();
        }
        var type = typeElement.GetString() ?? string.Empty;
        if (string.Equals(type, OpenCodeSessionStatusTypes.Idle, StringComparison.Ordinal))
        {
            return OpenCodeSessionStatus.Idle();
        }
        if (string.Equals(type, OpenCodeSessionStatusTypes.Busy, StringComparison.Ordinal))
        {
            return OpenCodeSessionStatus.Busy();
        }
        if (string.Equals(type, OpenCodeSessionStatusTypes.Retry, StringComparison.Ordinal))
        {
            if (!value.TryGetProperty("attempt", out var attemptElement) ||
                !attemptElement.TryGetInt32(out var attempt) ||
                attempt < 0 ||
                !value.TryGetProperty("message", out var messageElement) ||
                messageElement.ValueKind != JsonValueKind.String ||
                !value.TryGetProperty("next", out var nextElement) ||
                !nextElement.TryGetInt64(out var next) ||
                next < 0)
            {
                throw new OpenCodeSessionStatusPayloadException();
            }
            var message = messageElement.GetString() ?? string.Empty;
            try
            {
                OpenCodeSessionValidation.ValidateProviderText(
                    message,
                    OpenCodeSessionValidation.MaximumStatusMessageBytes,
                    "providerStatusMessage",
                    allowPromptWhitespace: true);
            }
            catch (ArgumentException)
            {
                throw new OpenCodeSessionStatusPayloadException();
            }
            return OpenCodeSessionStatus.Retry(attempt, message, next);
        }

        try
        {
            OpenCodeSessionValidation.ValidateProviderText(
                type,
                OpenCodeSessionValidation.MaximumUnknownStatusTypeBytes,
                "providerStatusType");
        }
        catch (ArgumentException)
        {
            throw new OpenCodeSessionStatusPayloadException();
        }
        return OpenCodeSessionStatus.Unknown(type);
    }

    private static void EnsureUniqueProperties(JsonElement source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new OpenCodeSessionStatusPayloadException();
            }
        }
    }
}

internal sealed class OpenCodeSessionStatusPayloadException : Exception
{
    public OpenCodeSessionStatusPayloadException()
        : base("OpenCode status payload is invalid.")
    {
    }
}
