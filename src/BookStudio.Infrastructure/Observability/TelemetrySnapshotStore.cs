using System.Collections.Concurrent;
using System.Globalization;
using BookStudio.Application.Observability;

namespace BookStudio.Infrastructure.Observability;

/// <summary>Bounded in-memory store for sanitized local operational telemetry.</summary>
public sealed class TelemetrySnapshotStore : IObservabilitySnapshotReader
{
    private static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "secret",
        "token",
        "authorization",
        "cookie",
        "path",
        "prompt",
        "content",
        "connection",
    ];

    private static readonly HashSet<string> AllowedAttributeKeys = new(StringComparer.Ordinal)
    {
        "bookstudio.operation.name",
        "bookstudio.operation.result",
        "bookstudio.safe_code",
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "server.address",
        "server.port",
        "network.protocol.version",
        "error.type",
        "event.id",
        "event.name",
        "{OriginalFormat}",
    };

    private readonly BoundedSignalBuffer<TraceSnapshotRecord> _traces;
    private readonly BoundedSignalBuffer<MetricSnapshotRecord> _metrics;
    private readonly BoundedSignalBuffer<LogSnapshotRecord> _logs;

    public TelemetrySnapshotStore(bool enabled, bool otlpEnabled, int capacityPerSignal)
    {
        if (capacityPerSignal is < 16 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityPerSignal));
        }

        Enabled = enabled;
        OtlpEnabled = otlpEnabled;
        CapacityPerSignal = capacityPerSignal;
        _traces = new BoundedSignalBuffer<TraceSnapshotRecord>(capacityPerSignal);
        _metrics = new BoundedSignalBuffer<MetricSnapshotRecord>(capacityPerSignal);
        _logs = new BoundedSignalBuffer<LogSnapshotRecord>(capacityPerSignal);
    }

    public bool Enabled { get; }
    public bool OtlpEnabled { get; }
    public int CapacityPerSignal { get; }

    public void RecordTrace(TraceSnapshotRecord record)
    {
        if (Enabled)
        {
            _traces.Add(record);
        }
    }

    public void RecordMetric(MetricSnapshotRecord record)
    {
        if (Enabled)
        {
            _metrics.Add(record);
        }
    }

    public void RecordLog(LogSnapshotRecord record)
    {
        if (Enabled)
        {
            _logs.Add(record);
        }
    }

    public ObservabilitySnapshot Read(int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return new ObservabilitySnapshot(
            Enabled,
            OtlpEnabled,
            CapacityPerSignal,
            _traces.Count,
            _metrics.Count,
            _logs.Count,
            _traces.DroppedCount,
            _metrics.DroppedCount,
            _logs.DroppedCount,
            _traces.ReadNewest(limit),
            _metrics.ReadNewest(limit),
            _logs.ReadNewest(limit));
    }

    public static IReadOnlyDictionary<string, string> SanitizeAttributes(
        IEnumerable<KeyValuePair<string, object?>>? attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (attributes is null)
        {
            return result;
        }

        foreach (var attribute in attributes)
        {
            var key = attribute.Key;
            if (string.IsNullOrWhiteSpace(key) ||
                IsSensitiveKey(key) ||
                !AllowedAttributeKeys.Contains(key))
            {
                continue;
            }

            var value = ConvertToSafeString(attribute.Value);
            if (value.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    public static string SanitizeTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "log";
        }

        var sanitized = RemoveControlCharacters(template.Trim());
        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    public static string SanitizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "Application";
        }

        var sanitized = RemoveControlCharacters(category.Trim());
        return sanitized.Length <= 256 ? sanitized : sanitized[..256];
    }

    private static bool IsSensitiveKey(string key)
    {
        return SensitiveKeyFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static string ConvertToSafeString(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var converted = value switch
        {
            string text => text,
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.GetType().Name,
        } ?? string.Empty;

        converted = RemoveControlCharacters(converted);
        return converted.Length <= 256 ? converted : converted[..256];
    }

    private static string RemoveControlCharacters(string value)
    {
        return string.Concat(value.Where(character => !char.IsControl(character)));
    }

    private sealed class BoundedSignalBuffer<T>
    {
        private readonly ConcurrentQueue<T> _records = new();
        private readonly int _capacity;
        private long _droppedCount;

        public BoundedSignalBuffer(int capacity)
        {
            _capacity = capacity;
        }

        public long Count => _records.Count;
        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        public void Add(T record)
        {
            _records.Enqueue(record);
            while (_records.Count > _capacity && _records.TryDequeue(out _))
            {
                Interlocked.Increment(ref _droppedCount);
            }
        }

        public IReadOnlyList<T> ReadNewest(int limit)
        {
            var snapshot = _records.ToArray();
            Array.Reverse(snapshot);
            return snapshot.Take(limit).ToArray();
        }
    }
}
