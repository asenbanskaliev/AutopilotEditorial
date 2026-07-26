namespace BookStudio.ControlCenter;

/// <summary>Validated OpenTelemetry and local snapshot configuration.</summary>
public sealed record ObservabilityOptions(
    bool Enabled,
    int SnapshotCapacityPerSignal,
    double TraceSamplingRatio,
    bool OtlpEnabled,
    Uri? OtlpEndpoint)
{
    public static ObservabilityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.GetValue("Observability:Enabled", true);
        var capacity = configuration.GetValue("Observability:SnapshotCapacityPerSignal", 256);
        var sampling = configuration.GetValue("Observability:TraceSamplingRatio", 1.0d);
        var otlpEnabled = configuration.GetValue("Observability:OtlpEnabled", false);
        var endpointValue = configuration["Observability:OtlpEndpoint"];
        Uri? endpoint = null;
        if (!string.IsNullOrWhiteSpace(endpointValue))
        {
            if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out endpoint))
            {
                throw new InvalidOperationException("Observability:OtlpEndpoint must be an absolute URI.");
            }
        }

        var options = new ObservabilityOptions(
            enabled,
            capacity,
            sampling,
            otlpEnabled,
            endpoint);
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (SnapshotCapacityPerSignal is < 16 or > 2_048)
        {
            throw new InvalidOperationException(
                "Observability:SnapshotCapacityPerSignal must be between 16 and 2048.");
        }

        if (double.IsNaN(TraceSamplingRatio) || TraceSamplingRatio is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "Observability:TraceSamplingRatio must be between 0 and 1.");
        }

        if (!OtlpEnabled)
        {
            return;
        }

        if (OtlpEndpoint is null)
        {
            throw new InvalidOperationException(
                "Observability:OtlpEndpoint is required when OTLP export is enabled.");
        }

        if (!string.IsNullOrEmpty(OtlpEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(OtlpEndpoint.Query) ||
            !string.IsNullOrEmpty(OtlpEndpoint.Fragment))
        {
            throw new InvalidOperationException(
                "The OTLP endpoint must not include credentials, query parameters or fragments.");
        }

        if (OtlpEndpoint.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (OtlpEndpoint.Scheme == Uri.UriSchemeHttp && OtlpEndpoint.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException(
            "OTLP export requires HTTPS, except loopback HTTP endpoints used by a local collector.");
    }
}
