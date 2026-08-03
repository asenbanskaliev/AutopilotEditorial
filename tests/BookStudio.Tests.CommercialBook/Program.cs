using BookStudio.Autopilot.EditorialJourney;

var options = EditorialJourneyProductionOptions.CreateDefault(Directory.GetCurrentDirectory(), "opencode");
var invoker = new FakeCommercialModelInvoker();
var planner = new OpenCodeCommercialBookPlanner(invoker, options);
var request = new FullBookProductionRequest(
    "vs138-commercial",
    "La ciudad que olvidaba los nombres",
    "es-ES",
    "Una archivera descubre que cada documento destruido borra un recuerdo colectivo.",
    8,
    900);

var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
Require(plan.Chapters.Count == 8, "commercial plan chapter count mismatch");
Require(plan.Chapters.Select(x => x.Number).SequenceEqual(Enumerable.Range(1, 8)), "commercial plan is not contiguous");

var generator = new OpenCodeCommercialChapterGenerator(invoker, options);
var chapters = new List<string>();
foreach (var chapter in plan.Chapters)
{
    var generated = await generator.GenerateAsync(request, chapter, "Contexto canónico acumulado.", CancellationToken.None);
    chapters.Add(generated.Markdown);
    Require(generated.Provider == "test-provider" && generated.Model == "test-model", "model metadata missing");
}
CommercialManuscriptPolicy.ValidateBook(chapters, 5600);
Require(chapters.Distinct(StringComparer.Ordinal).Count() == 8, "chapters were duplicated");

var repetitive = "# Capítulo 1: Repetición\n\n" + string.Join(". ", Enumerable.Repeat("La misma frase narrativa se repite sin aportar ningún cambio significativo a la escena", 80));
RequireThrows(() => CommercialManuscriptPolicy.ValidateChapter(repetitive, 1, 900), "repetitive chapter was accepted");
RequireThrows(() => CommercialManuscriptPolicy.ValidateChapter("# Capítulo 1\n\nplaceholder", 1, 900), "placeholder chapter was accepted");
Console.WriteLine("PASS VS-138 commercial book adapters and anti-placeholder policy");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void RequireThrows(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException(message);
}

sealed class FakeCommercialModelInvoker : IEditorialModelInvoker
{
    public ValueTask<EditorialModelExecution> InvokeAsync(string purpose, string prompt, string context, IReadOnlyList<EditorialModelCandidate> candidates, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (purpose == "commercial-book-plan")
        {
            var chapters = Enumerable.Range(1, 8).Select(number => new
            {
                number,
                title = $"El nombre perdido {number}",
                goal = $"La protagonista descubre una consecuencia irreversible del archivo {number}",
                continuityAnchor = $"Conservar la pista documental {number} y la evolución de su relación familiar",
            });
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                premise = "Una archivera lucha contra un sistema que convierte la destrucción documental en amnesia colectiva.",
                endingPromise = "La protagonista recuperará los nombres sin restaurar artificialmente el pasado.",
                chapters,
            });
            return ValueTask.FromResult(new EditorialModelExecution("test-provider", "test-model", "plan-hash", "context-hash", 10, json));
        }

        var chapterNumber = int.Parse(purpose[^2..], System.Globalization.CultureInfo.InvariantCulture);
        var paragraphs = Enumerable.Range(1, 24).Select(index =>
            $"La escena {index} del capítulo {chapterNumber} avanza cuando Mara compara el sello azul con la fecha del expediente {chapterNumber}-{index}, escucha una objeción distinta de su hermano y decide conservar una prueba que cambia la investigación de forma concreta");
        var content = $"# Capítulo {chapterNumber}: El nombre perdido {chapterNumber}\n\n" + string.Join(".\n\n", paragraphs) + ".";
        return ValueTask.FromResult(new EditorialModelExecution("test-provider", "test-model", $"chapter-{chapterNumber}-hash", "context-hash", 20, content));
    }
}
