using BookStudio.Autopilot.EditorialJourney;

var delay = new Delay(); var telemetry = new Sink(); var secret = "secret-live-key";
var executor = new ResilientOperationExecutor(new ResilientOperationOptions(3, 3, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5)), delay, telemetry, new SecretRedactor([secret]));
var calls = 0;
var value = await executor.ExecuteAsync("transient", _ =>
{
    calls++;
    if (calls < 3) throw new TransientOperationException("api_key=" + secret);
    return ValueTask.FromResult("ok");
});
Require(value == "ok" && calls == 3 && delay.Calls == 2, "transient retries failed");
Require(telemetry.Items.All(x => x.Error is null || !x.Error.Contains(secret, StringComparison.Ordinal)), "secret leaked to telemetry");

var breaker = new ResilientOperationExecutor(new ResilientOperationOptions(2, 2, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5)), delay, telemetry, new SecretRedactor([]));
try { await breaker.ExecuteAsync<int>("breaker", _ => throw new TransientOperationException("down")); } catch (TransientOperationException) { }
var opened = false;
try { await breaker.ExecuteAsync("breaker", _ => ValueTask.FromResult(1)); } catch (CircuitOpenException) { opened = true; }
Require(opened, "circuit did not open");

using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
var cancellationObserved = false;
try { await executor.ExecuteAsync("cancel", _ => ValueTask.FromResult(1), cancelled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
Require(cancellationObserved, "cancellation was swallowed");

var leases = new ProcessLeaseRegistry();
using (leases.Register(42)) Require(leases.Snapshot().SequenceEqual([42]), "lease not registered");
Require(leases.Snapshot().Count == 0, "lease not cleaned");
Console.WriteLine("PASS VS-136 reliability and operations");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed class Delay : IOperationDelay { public int Calls; public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { Calls++; return ValueTask.CompletedTask; } }
sealed class Sink : IOperationTelemetrySink { public List<OperationTelemetry> Items { get; } = []; public ValueTask WriteAsync(OperationTelemetry telemetry, CancellationToken cancellationToken) { Items.Add(telemetry); return ValueTask.CompletedTask; } }
