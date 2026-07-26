using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

var repositoryRoot = FindRepositoryRoot();
var errors = new List<string>();
var policyPath = Path.Combine(repositoryRoot, "docs", "architecture", "architecture-policy.json");

if (!File.Exists(policyPath))
{
    Console.Error.WriteLine("Architecture fitness FAIL: missing architecture-policy.json.");
    return 1;
}

var policy = JsonSerializer.Deserialize<ArchitecturePolicy>(
    File.ReadAllText(policyPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

if (policy is null)
{
    Console.Error.WriteLine("Architecture fitness FAIL: architecture policy could not be parsed.");
    return 1;
}

if (!string.Equals(policy.SchemaVersion, "1.0.0", StringComparison.Ordinal))
{
    errors.Add($"Unsupported architecture policy version: {policy.SchemaVersion}.");
}

var duplicateNames = policy.Projects
    .GroupBy(project => project.Name, StringComparer.Ordinal)
    .Where(group => group.Count() > 1)
    .Select(group => group.Key)
    .ToList();

var duplicatePaths = policy.Projects
    .GroupBy(project => project.ProjectPath, StringComparer.Ordinal)
    .Where(group => group.Count() > 1)
    .Select(group => group.Key)
    .ToList();

if (duplicateNames.Count > 0)
{
    errors.Add($"Duplicate policy project names: {string.Join(", ", duplicateNames)}.");
}

if (duplicatePaths.Count > 0)
{
    errors.Add($"Duplicate policy project paths: {string.Join(", ", duplicatePaths)}.");
}

foreach (var project in policy.Projects)
{
    ValidateProjectFile(repositoryRoot, project, errors);
    ValidateScopedInstructions(repositoryRoot, project, errors);
    ValidateCompiledAssembly(repositoryRoot, project, errors);
}

ValidateSolution(repositoryRoot, policy, errors);

if (errors.Count == 0)
{
    Console.WriteLine(
        $"Architecture fitness PASS: {policy.Projects.Count} projects, " +
        "project XML and compiled assembly references verified.");
    return 0;
}

foreach (var error in errors)
{
    Console.Error.WriteLine($"Architecture fitness FAIL: {error}");
}

return 1;

static void ValidateProjectFile(
    string repositoryRoot,
    ProjectPolicy project,
    ICollection<string> errors)
{
    var absolutePath = Path.Combine(repositoryRoot, project.ProjectPath);
    if (!File.Exists(absolutePath))
    {
        errors.Add($"Missing project: {project.ProjectPath}");
        return;
    }

    var document = XDocument.Load(absolutePath);
    var actualReferences = document
        .Descendants("ProjectReference")
        .Select(element => Normalize((string?)element.Attribute("Include") ?? string.Empty))
        .Where(value => value.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    var allowedReferences = project.AllowedProjectReferences.ToHashSet(StringComparer.Ordinal);
    if (!actualReferences.SetEquals(allowedReferences))
    {
        errors.Add(
            $"Invalid project references for {project.Name}. " +
            $"Expected [{string.Join(", ", allowedReferences.Order())}], " +
            $"actual [{string.Join(", ", actualReferences.Order())}].");
    }

    var packageReferences = document
        .Descendants("PackageReference")
        .Select(element => (string?)element.Attribute("Include") ?? string.Empty)
        .Where(value => value.Length > 0)
        .ToList();

    if (string.Equals(project.PackagePolicy, "none", StringComparison.Ordinal) &&
        packageReferences.Count > 0)
    {
        errors.Add(
            $"{project.Name} forbids package references but contains " +
            $"[{string.Join(", ", packageReferences)}].");
    }
}

static void ValidateScopedInstructions(
    string repositoryRoot,
    ProjectPolicy project,
    ICollection<string> errors)
{
    var agentsPath = Path.Combine(repositoryRoot, project.AgentsPath);
    if (!File.Exists(agentsPath))
    {
        errors.Add($"Missing scoped AGENTS instructions for {project.Name}: {project.AgentsPath}");
    }
}

static void ValidateCompiledAssembly(
    string repositoryRoot,
    ProjectPolicy project,
    ICollection<string> errors)
{
    var assemblyPath = Path.Combine(repositoryRoot, project.OutputAssemblyPath);
    if (!File.Exists(assemblyPath))
    {
        errors.Add($"Missing compiled assembly for {project.Name}: {project.OutputAssemblyPath}");
        return;
    }

    try
    {
        var actualReferences = ReadBookStudioAssemblyReferences(assemblyPath);
        var allowedReferences = project.AllowedBookStudioAssemblyReferences
            .ToHashSet(StringComparer.Ordinal);
        var forbiddenReferences = actualReferences
            .Except(allowedReferences, StringComparer.Ordinal)
            .Order()
            .ToList();

        if (forbiddenReferences.Count > 0)
        {
            errors.Add(
                $"{project.Name} contains forbidden compiled references: " +
                string.Join(", ", forbiddenReferences));
        }
    }
    catch (BadImageFormatException exception)
    {
        errors.Add($"Invalid PE assembly for {project.Name}: {exception.Message}");
    }
}

static HashSet<string> ReadBookStudioAssemblyReferences(string assemblyPath)
{
    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    if (!peReader.HasMetadata)
    {
        throw new BadImageFormatException($"Assembly has no metadata: {assemblyPath}");
    }

    var metadata = peReader.GetMetadataReader();
    return metadata.AssemblyReferences
        .Select(handle => metadata.GetAssemblyReference(handle))
        .Select(reference => metadata.GetString(reference.Name))
        .Where(name => name.StartsWith("BookStudio.", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);
}

static void ValidateSolution(
    string repositoryRoot,
    ArchitecturePolicy policy,
    ICollection<string> errors)
{
    var solutionPath = Path.Combine(repositoryRoot, "BookStudio.slnx");
    if (!File.Exists(solutionPath))
    {
        errors.Add("Missing BookStudio.slnx.");
        return;
    }

    var solutionProjects = XDocument.Load(solutionPath)
        .Descendants("Project")
        .Select(element => Normalize((string?)element.Attribute("Path") ?? string.Empty))
        .Where(value => value.Length > 0)
        .ToList();

    var policyProjects = policy.Projects
        .Select(project => project.ProjectPath)
        .ToHashSet(StringComparer.Ordinal);

    if (solutionProjects.Count != policyProjects.Count ||
        !solutionProjects.ToHashSet(StringComparer.Ordinal).SetEquals(policyProjects))
    {
        errors.Add("BookStudio.slnx and architecture policy project membership differ.");
    }
}

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

internal sealed record ArchitecturePolicy(
    string SchemaVersion,
    string PolicyName,
    List<ProjectPolicy> Projects);

internal sealed record ProjectPolicy(
    string Name,
    string ProjectPath,
    string Layer,
    string OutputAssemblyPath,
    List<string> AllowedProjectReferences,
    List<string> AllowedBookStudioAssemblyReferences,
    string PackagePolicy,
    string AgentsPath,
    List<string> ForbiddenNamespacePrefixes);
