from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        if new in text:
            return text
        raise SystemExit(f"missing anchor: {label}")
    return text.replace(old, new, 1)


reconciler_path = ROOT / "src/BookStudio.OpenCode/OpenCodeEventReconciler.cs"
text = reconciler_path.read_text(encoding="utf-8")
text = replace_once(
    text,
    "        var statuses = new Dictionary<string, OpenCodeSessionStatus>(StringComparer.Ordinal);",
    "        var statuses = new OpenCodeBoundedStatusCache(_options.MaximumStatusEntries);",
    "status cache construction",
)
text = replace_once(
    text,
    "                            statuses[provider.Event.SessionId] = provider.Event.Status;",
    "                            statuses.Set(provider.Event.SessionId, provider.Event.Status);",
    "provider status cache write",
)
text = replace_once(
    text,
    "                            if (statuses.TryGetValue(pair.Key, out var previous) && previous == pair.Value)",
    "                            if (statuses.TryGet(pair.Key, out var previous) && previous == pair.Value)",
    "poll status cache read",
)
text = replace_once(
    text,
    "                            statuses[pair.Key] = pair.Value;",
    "                            statuses.Set(pair.Key, pair.Value);",
    "poll status cache write",
)
cache_type = '''    private sealed class OpenCodeBoundedStatusCache
    {
        private readonly int _capacity;
        private readonly Queue<string> _order = new();
        private readonly Dictionary<string, OpenCodeSessionStatus> _values =
            new(StringComparer.Ordinal);

        public OpenCodeBoundedStatusCache(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _capacity = capacity;
        }

        public bool TryGet(string sessionId, out OpenCodeSessionStatus? status) =>
            _values.TryGetValue(sessionId, out status);

        public void Set(string sessionId, OpenCodeSessionStatus status)
        {
            if (_values.ContainsKey(sessionId))
            {
                _values[sessionId] = status;
                return;
            }
            if (_values.Count >= _capacity)
            {
                var expired = _order.Dequeue();
                _values.Remove(expired);
            }
            _values.Add(sessionId, status);
            _order.Enqueue(sessionId);
        }
    }

'''
text = replace_once(
    text,
    "    private abstract record InternalMessage;",
    cache_type + "    private abstract record InternalMessage;",
    "internal message anchor",
)
reconciler_path.write_text(text, encoding="utf-8")

journey_path = ROOT / "tests/BookStudio.Tests.OpenCodeSseReconciliation/OpenCodeSseReconciliationJourney.cs"
text = journey_path.read_text(encoding="utf-8")
text = replace_once(
    text,
    "        await DeduplicationAsync().ConfigureAwait(false);\n        await EofReconnectAndPollingAsync().ConfigureAwait(false);",
    "        await DeduplicationAsync().ConfigureAwait(false);\n        await StatusCacheBoundedAsync().ConfigureAwait(false);\n        await EofReconnectAndPollingAsync().ConfigureAwait(false);",
    "journey scenario registration",
)
scenario = '''    private async Task StatusCacheBoundedAsync()
    {
        var statusCalls = 0;
        await using var server = new ContractualOpenCodeSseServer((request, _) =>
        {
            if (request.Path == "/event")
            {
                return ValueTask.FromResult(ContractualSseResponse.Sse([
                    ContractualSseChunk.Utf8("data: {\\"type\\":\\"server.connected\\",\\"properties\\":{}}\\n\\n"),
                    ContractualSseChunk.Utf8("id: cache-a\\ndata: {\\"type\\":\\"session.status\\",\\"properties\\":{\\"sessionID\\":\\"ses_cache_a\\",\\"status\\":{\\"type\\":\\"busy\\"}}}\\n\\n"),
                    ContractualSseChunk.Utf8("id: cache-b\\ndata: {\\"type\\":\\"session.status\\",\\"properties\\":{\\"sessionID\\":\\"ses_cache_b\\",\\"status\\":{\\"type\\":\\"busy\\"}}}\\n\\n"),
                    ContractualSseChunk.Utf8("id: cache-c\\ndata: {\\"type\\":\\"session.status\\",\\"properties\\":{\\"sessionID\\":\\"ses_cache_c\\",\\"status\\":{\\"type\\":\\"busy\\"}}}\\n\\n"),
                ]));
            }
            if (request.Path == "/session/status")
            {
                var call = Interlocked.Increment(ref statusCalls);
                return ValueTask.FromResult(call == 1
                    ? StatusSnapshot()
                    : StatusSnapshot(("ses_cache_a", "busy")));
            }
            return ValueTask.FromResult(Route(request));
        });
        await using var reconciler = OpenCodeEventReconciler.Create(
            Endpoint(server),
            Options(
                maximumStatusEntries: 2,
                initialDelay: TimeSpan.FromMilliseconds(10),
                maximumDelay: TimeSpan.FromMilliseconds(20)));
        var items = await TakeUntilAsync(
            reconciler,
            new OpenCodeEventWatchRequest(OpenCodeEventScopes.Project),
            item => item.Source == OpenCodeEventSources.Poll &&
                    item.SessionId == "ses_cache_a" &&
                    item.Status?.Type == OpenCodeSessionStatusTypes.Busy &&
                    item.ReconciliationReason == OpenCodeReconciliationReasons.Eof,
            timeout: TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Require(statusCalls >= 2, "Bounded status-cache scenario did not poll after EOF.");
        Require(items.Count(item => item.SessionId == "ses_cache_a") == 2,
            "Status cache did not evict and re-observe the oldest session deterministically.");
        await RecordAndRequireClosedAsync(server).ConfigureAwait(false);
        _scenarios++;
        _events += items.Count;
    }

'''
text = replace_once(
    text,
    "    private async Task EofReconnectAndPollingAsync()",
    scenario + "    private async Task EofReconnectAndPollingAsync()",
    "EOF scenario anchor",
)
text = replace_once(
    text,
    "        int maximumFaults = 4,\n        TimeSpan? initialDelay = null,",
    "        int maximumFaults = 4,\n        int maximumStatusEntries = 100,\n        TimeSpan? initialDelay = null,",
    "options maximum status parameter",
)
text = replace_once(
    text,
    "            MaximumStatusEntries: 100,",
    "            MaximumStatusEntries: maximumStatusEntries,",
    "options maximum status assignment",
)
journey_path.write_text(text, encoding="utf-8")

governance_path = ROOT / "tests/governance/test_opencode_sse_reconciliation_contract.py"
text = governance_path.read_text(encoding="utf-8")
text = replace_once(
    text,
    '            "DeduplicationAsync",\n            "EofReconnectAndPollingAsync",',
    '            "DeduplicationAsync",\n            "StatusCacheBoundedAsync",\n            "EofReconnectAndPollingAsync",',
    "governance journey token",
)
text = replace_once(
    text,
    '            "Task.WhenAll",\n        ):',
    '            "Task.WhenAll",\n            "OpenCodeBoundedStatusCache",\n            "Queue<string>",\n        ):',
    "governance bounded cache tokens",
)
governance_path.write_text(text, encoding="utf-8")

remediation_path = ROOT / "docs/evidence/VS-032/AUDIT_REMEDIATION_001.md"
remediation_path.write_text(
    """# VS-032 — Audit Remediation 001\n\n"
    "## Finding\n\n"
    "M4 detected that each `/session/status` snapshot was bounded, but the cross-snapshot session-status history used an ordinary dictionary and could accumulate distinct session IDs for the lifetime of a watch.\n\n"
    "## Correction\n\n"
    "- replace the unbounded dictionary with a FIFO `OpenCodeBoundedStatusCache`;\n"
    "- capacity is exactly `MaximumStatusEntries`;\n"
    "- updates do not consume extra slots;\n"
    "- insertion at capacity evicts the oldest remembered session;\n"
    "- absence from a snapshot still does not imply idle, deletion or completion.\n\n"
    "## Executable proof\n\n"
    "`StatusCacheBoundedAsync` uses capacity 2, observes three SSE session IDs and then polls the first unchanged status after EOF. Re-emission proves deterministic eviction instead of unbounded retention.\n\n"
    "## Classification\n\n"
    "Product and test strengthening. No existing observable guarantee was removed.\n"
    """,
    encoding="utf-8",
)
