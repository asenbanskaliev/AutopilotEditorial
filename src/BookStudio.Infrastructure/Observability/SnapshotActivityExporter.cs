using System.Diagnostics;
using BookStudio.Application.Observability;
using OpenTelemetry;

namespace BookStudio.Infrastructure.Observability;

/// <summary>Exports completed activities into the bounded sanitized local snapshot.</summary>
public sealed class SnapshotActivityExporter : BaseExporter<Activity>
{
    private readonly TelemetrySnapshotStore _store;

    public SnapshotActivityExporter(TelemetrySnapshotStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            _store.RecordTrace(new TraceSnapshotRecord(
                new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
                activity.TraceId.ToHexString(),
                activity.SpanId.ToHexString(),
                TelemetrySnapshotStore.SanitizeTemplate(activity.DisplayName),
                activity.Kind.ToString(),
                activity.Status.ToString(),
                Math.Max(0, activity.Duration.TotalMilliseconds),
                TelemetrySnapshotStore.SanitizeAttributes(activity.TagObjects)));
        }

        return ExportResult.Success;
    }
}
