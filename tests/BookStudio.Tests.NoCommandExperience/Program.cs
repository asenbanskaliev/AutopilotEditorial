using BookStudio.Autopilot.EditorialJourney;

var root = Path.Combine(Path.GetTempPath(), "bookstudio-vs135-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var launcher = new Launcher();
    var first = new NoCommandEditorialExperience(new JsonEditorialConversationStore(root), launcher);
    var session = "session-135";
    Require((await first.SayAsync(session, "Una novela de misterio ambientada en Navarra")).Stage == EditorialConversationStage.Audience, "idea not accepted");
    Require((await first.SayAsync(session, "Lectores adultos de suspense")).Stage == EditorialConversationStage.Format, "audience not accepted");

    var restarted = new NoCommandEditorialExperience(new JsonEditorialConversationStore(root), launcher);
    Require((await restarted.SayAsync(session, "Novela")).Stage == EditorialConversationStage.Length, "session did not survive restart");
    var invalid = await restarted.SayAsync(session, "dos");
    Require(invalid.Stage == EditorialConversationStage.Length && invalid.Message.Contains("3 y 100", StringComparison.Ordinal), "invalid length not handled conversationally");
    Require((await restarted.SayAsync(session, "12 capítulos")).Stage == EditorialConversationStage.Tone, "length not accepted");
    Require((await restarted.SayAsync(session, "Sobrio, atmosférico y ágil")).Stage == EditorialConversationStage.Ready, "tone not accepted");
    var started = await restarted.SayAsync(session, "adelante");
    Require(started.Started && started.ProjectId == "book-session-135" && launcher.Calls == 1, "journey not launched once");
    var repeated = await restarted.SayAsync(session, "adelante");
    Require(repeated.Started && launcher.Calls == 1, "launch duplicated");
    var transcript = string.Join(' ', new[] { invalid.Message, started.Message, repeated.Message });
    Require(!transcript.Contains("MCP", StringComparison.OrdinalIgnoreCase) && !transcript.Contains("tool", StringComparison.OrdinalIgnoreCase) && !transcript.Contains("command", StringComparison.OrdinalIgnoreCase), "internal command vocabulary leaked");
    Console.WriteLine("PASS VS-135 no-command user experience");
}
finally { Directory.Delete(root, true); }

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
sealed class Launcher : INoCommandJourneyLauncher
{
    public int Calls { get; private set; }
    public ValueTask<string> StartAsync(EditorialConversationState state, CancellationToken cancellationToken)
    {
        Calls++;
        if (string.IsNullOrWhiteSpace(state.Idea) || string.IsNullOrWhiteSpace(state.Audience) || string.IsNullOrWhiteSpace(state.Format) || state.ChapterCount == 0 || string.IsNullOrWhiteSpace(state.Tone)) throw new InvalidOperationException("brief incomplete");
        return ValueTask.FromResult("book-" + state.SessionId);
    }
}
