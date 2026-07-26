namespace BookStudio.Application.Observability;

/// <summary>Reads a bounded sanitized operational snapshot without exposing provider internals.</summary>
public interface IObservabilitySnapshotReader
{
    ObservabilitySnapshot Read(int limit);
}
