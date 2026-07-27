using System.Text.Json;
using BookStudio.Application.OpenCode;

namespace BookStudio.OpenCode;

internal sealed record OpenCodeNormalizedProviderEvent(
    string Source,
    string Kind,
    string ProviderType,
    string? ProviderEventId,
    string? SessionId,
    string? Directory,
    OpenCodeSessionStatus? Status,
    byte[] ExactData);

internal static class OpenCodeEventNormalizer
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    public static OpenCodeNormalizedProviderEvent NormalizeProject(OpenCodeSseFrame frame) =>
        Normalize(frame, OpenCodeEventSources.Project, global: false);

    public static OpenCodeNormalizedProviderEvent NormalizeGlobal(OpenCodeSseFrame frame) =>
        Normalize(frame, OpenCodeEventSources.Global, global: true);

    private static OpenCodeNormalizedProviderEvent Normalize(
        OpenCodeSseFrame frame,
        string source,
        bool global)
    {
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            using var document = JsonDocument.Parse(frame.Data, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw InvalidPayload();
            }
            EnsureUniqueProperties(root);
            string? directory = null;
            JsonElement eventElement;
            if (global)
            {
                if (!root.TryGetProperty("directory", out var directoryElement) ||
                    directoryElement.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("payload", out eventElement) ||
                    eventElement.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidPayload();
                }
                directory = directoryElement.GetString() ?? string.Empty;
                ValidateProviderText(
                    directory,
                    OpenCodeEventValidation.MaximumDirectoryBytes,
                    "providerDirectory");
            }
            else
            {
                eventElement = root;
            }
            EnsureUniqueProperties(eventElement);
            if (!eventElement.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidPayload();
            }
            var providerType = typeElement.GetString() ?? string.Empty;
            ValidateProviderText(
                providerType,
                OpenCodeEventValidation.MaximumProviderTypeBytes,
                "providerEventType");

            if (string.Equals(providerType, "server.connected", StringComparison.Ordinal))
            {
                return new OpenCodeNormalizedProviderEvent(
                    source,
                    OpenCodeEventKinds.Connected,
                    providerType,
                    frame.Id,
                    null,
                    directory,
                    null,
                    frame.Data);
            }

            if (string.Equals(providerType, "session.status", StringComparison.Ordinal))
            {
                if (!eventElement.TryGetProperty("properties", out var properties) ||
                    properties.ValueKind != JsonValueKind.Object)
                {
                    throw InvalidPayload();
                }
                EnsureUniqueProperties(properties);
                if (!properties.TryGetProperty("sessionID", out var sessionElement) ||
                    sessionElement.ValueKind != JsonValueKind.String ||
                    !properties.TryGetProperty("status", out var statusElement))
                {
                    throw InvalidPayload();
                }
                var sessionId = sessionElement.GetString() ?? string.Empty;
                try
                {
                    OpenCodeSessionValidation.ValidateSessionId(sessionId, "providerSessionId");
                }
                catch (ArgumentException)
                {
                    throw InvalidPayload();
                }
                OpenCodeSessionStatus status;
                try
                {
                    status = OpenCodeSessionStatusParser.ParseStatus(statusElement);
                }
                catch (OpenCodeSessionStatusPayloadException)
                {
                    throw InvalidPayload();
                }
                return new OpenCodeNormalizedProviderEvent(
                    source,
                    OpenCodeEventKinds.SessionStatus,
                    providerType,
                    frame.Id,
                    sessionId,
                    directory,
                    status,
                    frame.Data);
            }

            return new OpenCodeNormalizedProviderEvent(
                source,
                OpenCodeEventKinds.ProviderEvent,
                providerType,
                frame.Id,
                TryReadBoundedSessionId(eventElement),
                directory,
                null,
                frame.Data);
        }
        catch (OpenCodeEventReconciliationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidPayload();
        }
    }

    private static string? TryReadBoundedSessionId(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        EnsureUniqueProperties(properties);
        if (!properties.TryGetProperty("sessionID", out var sessionElement) ||
            sessionElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var sessionId = sessionElement.GetString() ?? string.Empty;
        try
        {
            OpenCodeSessionValidation.ValidateSessionId(sessionId, "providerSessionId");
            return sessionId;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void ValidateProviderText(string value, int maximumBytes, string parameterName)
    {
        try
        {
            OpenCodeSessionValidation.ValidateProviderText(value, maximumBytes, parameterName);
        }
        catch (ArgumentException)
        {
            throw InvalidPayload();
        }
    }

    private static void EnsureUniqueProperties(JsonElement source)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw InvalidPayload();
            }
        }
    }

    private static OpenCodeEventReconciliationException InvalidPayload() =>
        new(OpenCodeEventErrorCodes.SsePayloadInvalid);
}
