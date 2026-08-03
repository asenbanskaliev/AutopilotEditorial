using System.Text.Json;
using System.Text.RegularExpressions;

namespace BookStudio.Autopilot.EditorialJourney;

public enum EditorialConversationStage { Idea, Audience, Format, Length, Tone, Ready, Running, Complete, Failed }
public sealed record EditorialConversationState(string SessionId, EditorialConversationStage Stage, string Idea, string Audience, string Format, int ChapterCount, string Tone, string? ProjectId, string? LastMessage, bool Started);
public sealed record EditorialConversationReply(string Message, EditorialConversationStage Stage, bool NeedsAnswer, bool Started, string? ProjectId);
public interface INoCommandJourneyLauncher { ValueTask<string> StartAsync(EditorialConversationState state, CancellationToken cancellationToken); }
public interface IEditorialConversationStore { ValueTask<EditorialConversationState?> LoadAsync(string sessionId, CancellationToken cancellationToken); ValueTask SaveAsync(EditorialConversationState state, CancellationToken cancellationToken); }

public sealed class JsonEditorialConversationStore : IEditorialConversationStore
{
    private readonly string _root;
    public JsonEditorialConversationStore(string root) { _root = Path.GetFullPath(root); Directory.CreateDirectory(_root); }
    public async ValueTask<EditorialConversationState?> LoadAsync(string sessionId, CancellationToken cancellationToken)
    {
        var path = PathFor(sessionId); if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<EditorialConversationState>(stream, cancellationToken: cancellationToken);
    }
    public async ValueTask SaveAsync(EditorialConversationState state, CancellationToken cancellationToken)
    {
        var path = PathFor(state.SessionId); var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        File.Move(temp, path, true);
    }
    private string PathFor(string id)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9-]{3,80}$")) throw new ArgumentException("Invalid session id.", nameof(id));
        return Path.Combine(_root, id + ".json");
    }
}

public sealed class NoCommandEditorialExperience
{
    private readonly IEditorialConversationStore _store;
    private readonly INoCommandJourneyLauncher _launcher;
    public NoCommandEditorialExperience(IEditorialConversationStore store, INoCommandJourneyLauncher launcher) { _store = store; _launcher = launcher; }

    public async ValueTask<EditorialConversationReply> SayAsync(string sessionId, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var state = await _store.LoadAsync(sessionId, cancellationToken) ?? new EditorialConversationState(sessionId, EditorialConversationStage.Idea, "", "", "", 0, "", null, null, false);
        if (state.Started) return new EditorialConversationReply(state.LastMessage ?? "El libro continúa en producción.", state.Stage, false, true, state.ProjectId);
        state = state.Stage switch
        {
            EditorialConversationStage.Idea => state with { Idea = message.Trim(), Stage = EditorialConversationStage.Audience, LastMessage = "¿Para qué tipo de lector quieres escribirlo?" },
            EditorialConversationStage.Audience => state with { Audience = message.Trim(), Stage = EditorialConversationStage.Format, LastMessage = "¿Será novela, ensayo, guía u otro formato?" },
            EditorialConversationStage.Format => state with { Format = message.Trim(), Stage = EditorialConversationStage.Length, LastMessage = "¿Cuántos capítulos aproximados deseas?" },
            EditorialConversationStage.Length => ParseLength(state, message),
            EditorialConversationStage.Tone => state with { Tone = message.Trim(), Stage = EditorialConversationStage.Ready, LastMessage = "Tengo todo lo necesario. Escribe ‘adelante’ para comenzar o indica qué dato quieres cambiar." },
            EditorialConversationStage.Ready => await StartOrAmendAsync(state, message, cancellationToken),
            _ => state,
        };
        await _store.SaveAsync(state, cancellationToken);
        return new EditorialConversationReply(state.LastMessage ?? "", state.Stage, state.Stage is not (EditorialConversationStage.Running or EditorialConversationStage.Complete), state.Started, state.ProjectId);
    }

    private static EditorialConversationState ParseLength(EditorialConversationState state, string message)
    {
        var match = Regex.Match(message, "\\d+");
        if (!match.Success || !int.TryParse(match.Value, out var count) || count is < 3 or > 100)
            return state with { LastMessage = "Indica un número de capítulos entre 3 y 100." };
        return state with { ChapterCount = count, Stage = EditorialConversationStage.Tone, LastMessage = "¿Qué tono y estilo deseas?" };
    }

    private async ValueTask<EditorialConversationState> StartOrAmendAsync(EditorialConversationState state, string message, CancellationToken cancellationToken)
    {
        var normalized = message.Trim().ToLowerInvariant();
        if (normalized is not ("adelante" or "comenzar" or "empieza" or "sí" or "si"))
            return state with { LastMessage = "Para comenzar escribe ‘adelante’. También puedes reiniciar esta conversación creando una sesión nueva." };
        var projectId = await _launcher.StartAsync(state, cancellationToken);
        return state with { ProjectId = projectId, Stage = EditorialConversationStage.Running, Started = true, LastMessage = "Tu libro ya está en producción. Guardaré el progreso y continuaré automáticamente tras cualquier reinicio." };
    }
}
