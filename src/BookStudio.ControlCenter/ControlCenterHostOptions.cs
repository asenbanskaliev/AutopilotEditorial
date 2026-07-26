namespace BookStudio.ControlCenter;

/// <summary>Validated local-host configuration for the Control Center API.</summary>
public sealed record ControlCenterHostOptions(
    string Url,
    string WorkspaceRoot,
    bool AllowRemoteBinding)
{
    public const string DefaultUrl = "http://127.0.0.1:5074";

    public static ControlCenterHostOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaultWorkspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BookStudio",
            "workspace");
        var options = new ControlCenterHostOptions(
            configuration["ControlCenter:Url"] ?? DefaultUrl,
            Path.GetFullPath(configuration["ControlCenter:WorkspaceRoot"] ?? defaultWorkspace),
            configuration.GetValue("ControlCenter:AllowRemoteBinding", false));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("ControlCenter:Url must be an absolute HTTP or HTTPS URL.");
        }

        if (!AllowRemoteBinding && !uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Remote Control Center binding is disabled. Use loopback or explicitly enable ControlCenter:AllowRemoteBinding.");
        }

        if (string.IsNullOrWhiteSpace(WorkspaceRoot))
        {
            throw new InvalidOperationException("ControlCenter:WorkspaceRoot is required.");
        }
    }
}
