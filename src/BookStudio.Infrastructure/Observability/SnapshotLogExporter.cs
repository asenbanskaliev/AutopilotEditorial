using System.Globalization;
using BookStudio.Application.Observability;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace BookStudio.Infrastructure.Observability;

/// <summary>Exports structured log templates and allowlisted attributes without messages or stacks.</summary>
public sealed class SnapshotLogExporter : BaseExporter<LogRecord>
{
    private readonly TelemetrySnapshotStore _store;

    public SnapshotLogExporter(TelemetrySnapshotStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        foreach (var record in batch)
        {
            var template = Convert.ToString(record.Body, CultureInfo.InvariantCulture);
            _store.RecordLog(new LogSnapshotRecord(
                record.Timestamp,
                record.LogLevel.ToString(),
                TelemetrySnapshotStore.SanitizeCategory(record.CategoryName),
                TelemetrySnapshotStore.SanitizeTemplate(template),
                record.TraceId == default ? null : record.TraceId.ToHexString(),
                record.Exception?.GetType().FullName,
                TelemetrySnapshotStore.SanitizeAttributes(record.Attributes)));
        }

        return ExportResult.Success;
    }
}
