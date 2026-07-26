using BookStudio.Application.Observability;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace BookStudio.Infrastructure.Observability;

/// <summary>Exports metric descriptors into a bounded local operational snapshot.</summary>
public sealed class SnapshotMetricExporter : BaseExporter<Metric>
{
    private readonly TelemetrySnapshotStore _store;

    public SnapshotMetricExporter(TelemetrySnapshotStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override ExportResult Export(in Batch<Metric> batch)
    {
        var exportedAtUtc = DateTimeOffset.UtcNow;
        foreach (var metric in batch)
        {
            _store.RecordMetric(new MetricSnapshotRecord(
                exportedAtUtc,
                TelemetrySnapshotStore.SanitizeTemplate(metric.Name),
                metric.MetricType.ToString(),
                TelemetrySnapshotStore.SanitizeTemplate(metric.Unit)));
        }

        return ExportResult.Success;
    }
}
