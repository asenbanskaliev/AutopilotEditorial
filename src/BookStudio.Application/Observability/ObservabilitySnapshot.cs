namespace BookStudio.Application.Observability;

public sealed record ObservabilitySnapshot(
    bool Enabled,
    bool OtlpEnabled,
    int CapacityPerSignal,
    long TraceCount,
    long MetricCount,
    long LogCount,
    long DroppedTraceCount,
    long DroppedMetricCount,
    long DroppedLogCount,
    IReadOnlyList<TraceSnapshotRecord> Traces,
    IReadOnlyList<MetricSnapshotRecord> Metrics,
    IReadOnlyList<LogSnapshotRecord> Logs);

public sealed record TraceSnapshotRecord(
    DateTimeOffset StartedAtUtc,
    string TraceId,
    string SpanId,
    string Name,
    string Kind,
    string Status,
    double DurationMilliseconds,
    IReadOnlyDictionary<string, string> Attributes);

public sealed record MetricSnapshotRecord(
    DateTimeOffset ExportedAtUtc,
    string Name,
    string InstrumentType,
    string Unit);

public sealed record LogSnapshotRecord(
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string MessageTemplate,
    string? TraceId,
    string? ExceptionType,
    IReadOnlyDictionary<string, string> Attributes);
