using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Autopilot.EditorialJourney;

public sealed class ResilientOpenCodeEditorialModelInvoker : IEditorialModelInvoker
{
    private readonly EditorialJourneyProductionOptions _options;
    private readonly ConcurrentDictionary<string, EditorialModelExecution> _cache = new(StringComparer.Ordinal);

    public ResilientOpenCodeEditorialModelInvoker(EditorialJourneyProductionOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    public async ValueTask<EditorialModelExecution> InvokeAsync(
        string purpose,
        string prompt,
        string context,
        IReadOnlyList<EditorialModelCandidate> candidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(candidates);
        var safeContext = context ?? string.Empty;
        var promptHash = Hash(prompt);
        var contextHash = Hash(safeContext);
        var approved = candidates.Where(x => x.IsFree).OrderBy(x => x.Priority).ToArray();
        if (approved.Length == 0)
        {
            throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "model_candidates_empty", "No approved free model candidates were supplied.");
        }
        var key = $"{purpose}|{promptHash}|{contextHash}|{string.Join(',', approved.Select(x => x.ModelId))}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var failures = new List<string>();
        foreach (var candidate in approved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"MODEL START purpose={purpose} model={candidate.ModelId}");
            try
            {
                var result = await InvokeOneAsync(candidate.ModelId, prompt, safeContext, promptHash, contextHash, timeout, cancellationToken)
                    .ConfigureAwait(false);
                _cache.TryAdd(key, result);
                Console.WriteLine($"MODEL PASS purpose={purpose} model={candidate.ModelId} durationMs={result.DurationMilliseconds}");
                return result;
            }
            catch (TimeoutException)
            {
                failures.Add($"{candidate.ModelId}:timeout");
                Console.WriteLine($"MODEL FALLBACK purpose={purpose} model={candidate.ModelId} reason=timeout");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"{candidate.ModelId}:{exception.GetType().Name}");
                Console.WriteLine($"MODEL FALLBACK purpose={purpose} model={candidate.ModelId} reason={exception.GetType().Name}");
            }
        }
        throw new EditorialJourneyException(EditorialJourneyStage.Briefing, "all_models_failed", $"All approved models failed for {purpose}: {string.Join(',', failures)}");
    }

    private async ValueTask<EditorialModelExecution> InvokeOneAsync(
        string model,
        string prompt,
        string context,
        string promptHash,
        string contextHash,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = _options.OpenCodeExecutable,
            WorkingDirectory = _options.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(model);
        start.ArgumentList.Add(string.IsNullOrWhiteSpace(context) ? prompt : $"CONTEXTO CANONICO:\n{context}\n\nTAREA:\n{prompt}");

        using var process = new Process { StartInfo = start };
        var watch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("OpenCode process could not be started.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            Kill(process);
            throw new TimeoutException($"OpenCode model {model} exceeded {timeout.TotalSeconds:0}s.");
        }
        catch
        {
            Kill(process);
            throw;
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        watch.Stop();
        if (process.ExitCode != 0) throw new InvalidOperationException($"OpenCode exited with {process.ExitCode}: {Sanitize(stderr)}");
        var content = stdout.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
        if (content.Length < 80) throw new InvalidOperationException("OpenCode returned unexpectedly short content.");
        return new EditorialModelExecution(_options.OpenCodeProvider, model, promptHash, contextHash, watch.ElapsedMilliseconds, content);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sanitize(string value) => value.Length <= 800 ? value : value[^800..];
}
