using BookStudio.Autopilot.EditorialJourney;

var center = new HumanEditorialControlCenter(new InMemoryEditorialControlAuditStore());
var blocked = await center.ApplyAsync("book-141", EditorialControlStage.GlobalReview, EditorialControlDecision.Block, "editor@example", "Contradiction between chapters 2 and 8", "sha256:block", CancellationToken.None);
Require(blocked.IsBlocked && blocked.BlockReason is not null, "block state missing");
await RequireThrowsAsync(() => center.ApplyAsync("book-141", EditorialControlStage.Production, EditorialControlDecision.Approve, "publisher@example", "approve", "sha256:bad", CancellationToken.None), "blocked journey advanced without resume");
var resumed = await center.ApplyAsync("book-141", EditorialControlStage.GlobalReview, EditorialControlDecision.Resume, "editor@example", "Contradiction repaired", "sha256:repair", CancellationToken.None);
Require(!resumed.IsBlocked, "resume did not clear block");
var approved = await center.ApplyAsync("book-141", EditorialControlStage.Production, EditorialControlDecision.Approve, "publisher@example", "Production package accepted", "sha256:package", CancellationToken.None);
Require(approved.IsApproved && approved.History.Count == 3, "approval history incomplete");
await RequireThrowsAsync(() => center.ApplyAsync("book-141", EditorialControlStage.Outline, EditorialControlDecision.Revise, "editor@example", "move backwards", "sha256:old", CancellationToken.None), "backward transition accepted");
Console.WriteLine("PASS VS-141 human editorial control center");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task RequireThrowsAsync(Func<ValueTask<EditorialControlSnapshot>> action, string message)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException(message);
}
