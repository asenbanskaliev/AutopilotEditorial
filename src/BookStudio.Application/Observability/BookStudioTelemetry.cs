using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace BookStudio.Application.Observability;

/// <summary>Low-cardinality custom tracing and metrics for BookStudio operations.</summary>
public static class BookStudioTelemetry
{
    public const string InstrumentationName = "BookStudio";
    public const string OperationNameTag = "bookstudio.operation.name";
    public const string OperationResultTag = "bookstudio.operation.result";

    public static readonly ActivitySource ActivitySource = new(InstrumentationName);
    public static readonly Meter Meter = new(InstrumentationName);

    private static readonly Counter<long> OperationCounter = Meter.CreateCounter<long>(
        "bookstudio.operations",
        unit: "{operation}",
        description: "Completed BookStudio operations.");
    private static readonly Counter<long> OperationFailureCounter = Meter.CreateCounter<long>(
        "bookstudio.operation.failures",
        unit: "{failure}",
        description: "Failed BookStudio operations.");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "bookstudio.operation.duration",
        unit: "ms",
        description: "BookStudio operation duration.");
    private static readonly UpDownCounter<long> ActiveOperations = Meter.CreateUpDownCounter<long>(
        "bookstudio.operations.active",
        unit: "{operation}",
        description: "Currently active BookStudio operations.");

    public static Activity? StartOperation(string operationName)
    {
        operationName = ValidateOperationName(operationName);
        var activity = ActivitySource.StartActivity("bookstudio.operation", ActivityKind.Internal);
        activity?.SetTag(OperationNameTag, operationName);
        ActiveOperations.Add(1, new KeyValuePair<string, object?>(OperationNameTag, operationName));
        return activity;
    }

    public static void CompleteOperation(
        string operationName,
        TimeSpan duration,
        bool succeeded,
        Activity? activity = null)
    {
        operationName = ValidateOperationName(operationName);
        var tags = new TagList
        {
            { OperationNameTag, operationName },
            { OperationResultTag, succeeded ? "success" : "failure" },
        };
        OperationCounter.Add(1, tags);
        OperationDuration.Record(Math.Max(0, duration.TotalMilliseconds), tags);
        if (!succeeded)
        {
            OperationFailureCounter.Add(1, tags);
        }
        ActiveOperations.Add(-1, new KeyValuePair<string, object?>(OperationNameTag, operationName));
        activity?.SetTag(OperationResultTag, succeeded ? "success" : "failure");
        activity?.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);
        activity?.Stop();
    }

    private static string ValidateOperationName(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        if (operationName.Length > 64 ||
            operationName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new ArgumentException(
                "Operation names must be low-cardinality ASCII tokens of at most 64 characters.",
                nameof(operationName));
        }
        return operationName;
    }
}
