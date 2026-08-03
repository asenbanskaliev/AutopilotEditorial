using BookStudio.Autopilot.EditorialJourney;

var passing = Evidence();
var result = RealLongRunningAcceptanceGate.Evaluate(passing);
Require(result.Passed, string.Join(',', result.BlockingReasons));

var shortRun = RealLongRunningAcceptanceGate.Evaluate(passing with { Elapsed = TimeSpan.FromMinutes(30) });
Require(!shortRun.Passed && shortRun.BlockingReasons.Contains("elapsed_below_full_scale"), "short run was accepted");
var lowWords = RealLongRunningAcceptanceGate.Evaluate(passing with { GeneratedWords = 29_999 });
Require(lowWords.BlockingReasons.Contains("word_count_below_full_scale"), "low word count was accepted");
var duplicate = RealLongRunningAcceptanceGate.Evaluate(passing with { DuplicateChapters = 1 });
Require(duplicate.BlockingReasons.Contains("duplicate_content_detected"), "duplicate content was accepted");
var secret = RealLongRunningAcceptanceGate.Evaluate(passing with { SecretLeakageDetected = true });
Require(secret.BlockingReasons.Contains("secret_leakage_detected"), "secret leakage was accepted");
Console.WriteLine("PASS VS-143 real long-running acceptance gate");

static LongRunningModelAcceptanceEvidence Evidence() => new(
    "vs143-live-book", true, true, TimeSpan.FromHours(2.5), 36_420, 12, 12, true,
    4, 2, 2, 8, true, true, true, true, 0, 0, 0m, 7_200_000,
    false, false, new string('a', 64));
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
