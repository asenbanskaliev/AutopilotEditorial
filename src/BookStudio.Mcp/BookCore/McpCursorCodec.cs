using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Mcp.BookCore;

public sealed class McpCursorException : Exception
{
    public McpCursorException(string message) : base(message) { }
}

/// <summary>Opaque versioned cursor scoped to one immutable catalog fingerprint.</summary>
public static class McpCursorCodec
{
    private const string Version = "v1";

    public static string Encode(
        string scope,
        int offset,
        string catalogFingerprint)
    {
        ValidateParts(scope, offset, catalogFingerprint);
        var payload = $"{Version}|{scope}|{offset}|{catalogFingerprint}";
        var checksum = Checksum(payload);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload + "|" + checksum));
    }

    public static int Decode(
        string cursor,
        string expectedScope,
        string expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 512)
        {
            throw new McpCursorException("Cursor is missing or exceeds its limit.");
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
        }
        catch (Exception exception) when (
            exception is FormatException or DecoderFallbackException)
        {
            throw new McpCursorException("Cursor encoding is invalid.");
        }

        var parts = decoded.Split('|');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], Version, StringComparison.Ordinal) ||
            !string.Equals(parts[1], expectedScope, StringComparison.Ordinal) ||
            !int.TryParse(parts[2], out var offset) ||
            offset < 0 ||
            !string.Equals(parts[3], expectedFingerprint, StringComparison.Ordinal))
        {
            throw new McpCursorException("Cursor scope, version, offset or catalog fingerprint is invalid.");
        }

        var payload = string.Join('|', parts[..4]);
        var expectedChecksum = Checksum(payload);
        var supplied = Encoding.ASCII.GetBytes(parts[4]);
        var expected = Encoding.ASCII.GetBytes(expectedChecksum);
        if (supplied.Length != expected.Length ||
            !CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            throw new McpCursorException("Cursor checksum is invalid.");
        }

        return offset;
    }

    private static void ValidateParts(
        string scope,
        int offset,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(scope) ||
            scope.Length > 32 ||
            scope.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Cursor scope is invalid.", nameof(scope));
        }
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (string.IsNullOrWhiteSpace(fingerprint) ||
            fingerprint.Length > 64 ||
            fingerprint.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Catalog fingerprint is invalid.", nameof(fingerprint));
        }
    }

    private static string Checksum(string payload) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant()[..16];

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Base64Url length."),
        };
        return Convert.FromBase64String(padded);
    }
}
