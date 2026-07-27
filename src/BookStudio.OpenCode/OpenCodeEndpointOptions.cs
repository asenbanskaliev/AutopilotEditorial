namespace BookStudio.OpenCode;

/// <summary>Validated HTTP endpoint, optional Basic authentication and bounded compatibility-probe limits.</summary>
public sealed record OpenCodeEndpointOptions(
    Uri BaseUri,
    string? Username,
    string? Password,
    TimeSpan RequestTimeout,
    int MaximumHealthBytes,
    int MaximumSpecificationBytes)
{
    public const int DefaultMaximumHealthBytes = 16 * 1024;
    public const int DefaultMaximumSpecificationBytes = 2 * 1024 * 1024;

    public static OpenCodeEndpointOptions Create(
        string baseUrl,
        string? username = null,
        string? password = null,
        TimeSpan? requestTimeout = null,
        int maximumHealthBytes = DefaultMaximumHealthBytes,
        int maximumSpecificationBytes = DefaultMaximumSpecificationBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException("OpenCode base URL must be absolute.", nameof(baseUrl));
        }
        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("OpenCode base URL must use HTTP or HTTPS.", nameof(baseUrl));
        }
        if (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !baseUri.IsLoopback)
        {
            throw new ArgumentException("Plain HTTP is allowed only for loopback OpenCode servers.", nameof(baseUrl));
        }
        if (!string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment) ||
            baseUri.AbsolutePath is not "/")
        {
            throw new ArgumentException("OpenCode base URL must not contain credentials, path, query or fragment.", nameof(baseUrl));
        }

        ValidateCredential(username, 128, nameof(username));
        ValidateCredential(password, 512, nameof(password));
        if ((username is null) != (password is null))
        {
            throw new ArgumentException("OpenCode Basic authentication requires both username and password.");
        }

        var timeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout < TimeSpan.FromMilliseconds(100) || timeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        if (maximumHealthBytes is < 256 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumHealthBytes));
        }
        if (maximumSpecificationBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSpecificationBytes));
        }

        return new OpenCodeEndpointOptions(
            new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute),
            username,
            password,
            timeout,
            maximumHealthBytes,
            maximumSpecificationBytes);
    }

    private static void ValidateCredential(
        string? value,
        int maximumLength,
        string parameterName)
    {
        if (value is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException("OpenCode credential field is invalid.", parameterName);
        }
    }
}
