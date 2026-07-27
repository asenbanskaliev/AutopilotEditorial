using System.Text;

namespace BookStudio.Application.OpenCode;

/// <summary>Shared provider-neutral validation and byte bounds for OpenCode session commands.</summary>
public static class OpenCodeSessionValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public const int MaximumSessionIdBytes = 128;
    public const int MaximumIdempotencyKeyBytes = 128;
    public const int MaximumTitleBytes = 512;
    public const int MaximumPromptPartCount = 64;
    public const int MaximumTextPartBytes = 64 * 1024;
    public const int MaximumPromptBytes = 256 * 1024;
    public const int MaximumStatusEntries = 10_000;
    public const int MaximumStatusMessageBytes = 2 * 1024;
    public const int MaximumUnknownStatusTypeBytes = 64;

    public static void ValidateCreateCommand(OpenCodeCreateSessionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ParentSessionId is not null)
        {
            ValidateSessionId(command.ParentSessionId, nameof(command.ParentSessionId));
        }
        if (command.Title is not null)
        {
            ValidateTitle(command.Title, nameof(command.Title));
        }
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
    }

    public static void ValidatePromptCommand(OpenCodeSendPromptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSessionId(command.SessionId, nameof(command.SessionId));
        ValidateIdempotencyKey(command.IdempotencyKey, nameof(command.IdempotencyKey));
        ValidateTextParts(command.Parts, nameof(command.Parts));
    }

    public static void ValidateSessionId(
        string sessionId,
        string parameterName = "sessionId")
    {
        ValidateRequiredBoundedText(
            sessionId,
            MaximumSessionIdBytes,
            parameterName,
            allowPromptWhitespace: false);
        if (sessionId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-'))
        {
            throw new ArgumentException(
                "OpenCode session ID contains an unsafe character.",
                parameterName);
        }
    }

    public static void ValidateIdempotencyKey(
        string idempotencyKey,
        string parameterName = "idempotencyKey")
    {
        ValidateRequiredBoundedText(
            idempotencyKey,
            MaximumIdempotencyKeyBytes,
            parameterName,
            allowPromptWhitespace: false);
        if (idempotencyKey.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-' and not '.' and not ':'))
        {
            throw new ArgumentException(
                "OpenCode idempotency key contains an unsafe character.",
                parameterName);
        }
    }

    public static void ValidateTitle(
        string title,
        string parameterName = "title")
    {
        ValidateRequiredBoundedText(
            title,
            MaximumTitleBytes,
            parameterName,
            allowPromptWhitespace: false);
    }

    public static void ValidateTextParts(
        IReadOnlyList<OpenCodeTextPart> parts,
        string parameterName = "parts")
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count is < 1 or > MaximumPromptPartCount)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        var totalBytes = 0;
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index] ?? throw new ArgumentException(
                "OpenCode prompt part cannot be null.",
                parameterName);
            ValidateRequiredBoundedText(
                part.Text,
                MaximumTextPartBytes,
                parameterName,
                allowPromptWhitespace: true);
            totalBytes = checked(totalBytes + GetUtf8ByteCount(part.Text, parameterName));
            if (totalBytes > MaximumPromptBytes)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }

    public static void ValidateProviderText(
        string value,
        int maximumBytes,
        string parameterName,
        bool allowPromptWhitespace = false)
    {
        ValidateRequiredBoundedText(
            value,
            maximumBytes,
            parameterName,
            allowPromptWhitespace);
    }

    public static int GetUtf8ByteCount(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "OpenCode text contains invalid Unicode.",
                parameterName,
                exception);
        }
    }

    private static void ValidateRequiredBoundedText(
        string value,
        int maximumBytes,
        string parameterName,
        bool allowPromptWhitespace)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value) ||
            (!allowPromptWhitespace && !string.Equals(value, value.Trim(), StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "OpenCode text must be non-empty and canonical.",
                parameterName);
        }
        foreach (var character in value)
        {
            if (!char.IsControl(character))
            {
                continue;
            }
            if (allowPromptWhitespace && character is '\r' or '\n' or '\t')
            {
                continue;
            }
            throw new ArgumentException(
                "OpenCode text contains a forbidden control character.",
                parameterName);
        }
        if (GetUtf8ByteCount(value, parameterName) > maximumBytes)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
