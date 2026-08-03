using BookStudio.Autopilot.EditorialJourney;

var dimensions = Enum.GetValues<ProductionReadinessDimension>()
    .Select(d => new ProductionReadinessDimensionResult(d, true, [$"evidence/{d}.json"]))
    .ToArray();
var report = new ProductionReadinessAuditReport(
    "release-144",
    "independent-auditor",
    true,
    dimensions,
    [new(ProductionReadinessDimension.Documentation, AuditFindingSeverity.Low, "DOC-001", "Minor wording improvement", "docs/readiness.md", false)],
    "Residual low risks are documented and accepted for release.",
    true,
    new string('b', 64),
    DateTimeOffset.UtcNow);
var pass = ProductionReadinessAuditGate.Evaluate(report);
Require(pass.Passed, string.Join(',', pass.BlockingReasons));

var missing = ProductionReadinessAuditGate.Evaluate(report with { Dimensions = dimensions.Where(x => x.Dimension != ProductionReadinessDimension.Security).ToArray() });
Require(missing.BlockingReasons.Any(x => x.StartsWith("dimension_missing:Security", StringComparison.Ordinal)), "missing security dimension accepted");
var critical = ProductionReadinessAuditGate.Evaluate(report with { Findings = [new(ProductionReadinessDimension.Security, AuditFindingSeverity.Critical, "SEC-999", "Critical issue", "evidence/sec.json", false)] });
Require(critical.BlockingReasons.Contains("unresolved_critical:SEC-999"), "critical finding accepted");
var dependent = ProductionReadinessAuditGate.Evaluate(report with { AuditorIndependent = false });
Require(dependent.BlockingReasons.Contains("independent_auditor_missing"), "non-independent audit accepted");
Console.WriteLine("PASS VS-144 production readiness audit gate");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
