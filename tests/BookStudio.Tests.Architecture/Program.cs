using System.Xml.Linq;

var repositoryRoot = FindRepositoryRoot();
var errors = new List<string>();

var expectedReferences = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
{
    ["src/BookStudio.Domain/BookStudio.Domain.csproj"] = [],
    ["src/BookStudio.Application/BookStudio.Application.csproj"] =
    [
        "../BookStudio.Domain/BookStudio.Domain.csproj",
    ],
    ["src/BookStudio.Infrastructure/BookStudio.Infrastructure.csproj"] =
    [
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Domain/BookStudio.Domain.csproj",
    ],
    ["src/BookStudio.Mcp/BookStudio.Mcp.csproj"] =
    [
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
    ],
    ["src/BookStudio.OpenCode/BookStudio.OpenCode.csproj"] =
    [
        "../BookStudio.Application/BookStudio.Application.csproj",
    ],
    ["src/BookStudio.Autopilot/BookStudio.Autopilot.csproj"] =
    [
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Domain/BookStudio.Domain.csproj",
    ],
    ["src/BookStudio.Worker/BookStudio.Worker.csproj"] =
    [
        "../BookStudio.Autopilot/BookStudio.Autopilot.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
        "../BookStudio.OpenCode/BookStudio.OpenCode.csproj",
    ],
    ["src/BookStudio.ControlCenter/BookStudio.ControlCenter.csproj"] =
    [
        "../BookStudio.Application/BookStudio.Application.csproj",
        "../BookStudio.Infrastructure/BookStudio.Infrastructure.csproj",
    ],
    ["tests/BookStudio.Tests.Architecture/BookStudio.Tests.Architecture.csproj"] = [],
};

foreach (var (projectPath, expected) in expectedReferences)
{
    var absolutePath = Path.Combine(repositoryRoot, projectPath);
    if (!File.Exists(absolutePath))
    {
        errors.Add($"Missing project: {projectPath}");
        continue;
    }

    var actual = XDocument.Load(absolutePath)
        .Descendants("ProjectReference")
        .Select(element => Normalize((string?)element.Attribute("Include") ?? string.Empty))
        .Where(value => value.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    if (!actual.SetEquals(expected))
    {
        errors.Add(
            $"Invalid references for {projectPath}. " +
            $"Expected [{string.Join(", ", expected.Order())}], " +
            $"actual [{string.Join(", ", actual.Order())}].");
    }
}

var solutionPath = Path.Combine(repositoryRoot, "BookStudio.slnx");
if (!File.Exists(solutionPath))
{
    errors.Add("Missing BookStudio.slnx.");
}
else
{
    var solutionProjects = XDocument.Load(solutionPath)
        .Descendants("Project")
        .Select(element => Normalize((string?)element.Attribute("Path") ?? string.Empty))
        .Where(value => value.Length > 0)
        .ToList();

    if (solutionProjects.Count != expectedReferences.Count ||
        !solutionProjects.ToHashSet(StringComparer.Ordinal).SetEquals(expectedReferences.Keys))
    {
        errors.Add("BookStudio.slnx does not contain every expected project exactly once.");
    }
}

if (errors.Count == 0)
{
    Console.WriteLine($"Architecture fitness PASS: {expectedReferences.Count} projects.");
    return 0;
}

foreach (var error in errors)
{
    Console.Error.WriteLine($"Architecture fitness FAIL: {error}");
}

return 1;

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
         directory is not null;
         directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BookStudio.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Could not locate the repository root.");
}

static string Normalize(string value) => value.Replace('\\', '/');
