using System.IO.Compression;
using BookStudio.Application.Authoring;

namespace BookStudio.Tests.Integration;

internal static class PublicationArtifactPipelineIntegrationSmoke
{
    public static async Task RunAsync(string workspaceRoot)
    {
        var proofId = Guid.NewGuid();
        var request = new PublicationArtifactRequest(
            proofId,
            "workspace-vs123",
            "The Clockmaker's Map",
            "Autopilot Editorial",
            "en",
            "Chapter One\nThe clock stopped at midnight, but the map continued to move.",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EPUB", "PDF", "DOCX", "KDP" },
            0m,
            "EUR");

        var provider = new LocalDeterministicPublicationProvider();
        var pipeline = new PublicationArtifactPipeline([provider]);
        var first = await pipeline.ProduceAsync(request, provider.ProviderId, workspaceRoot);
        Require(first.Artifacts.Count == 4, "all four publication formats must be produced");
        Require(first.Cost == 0m, "local reference provider must remain inside the zero-cost quote");

        foreach (var artifact in first.Artifacts)
        {
            var path = Path.Combine(workspaceRoot, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Require(File.Exists(path), $"{artifact.Format} artifact must exist");
            Require(new FileInfo(path).Length == artifact.ByteSize, $"{artifact.Format} size evidence must match bytes");
            Require(artifact.Verified && artifact.Provenance.Contains(provider.ProviderId, StringComparison.Ordinal), $"{artifact.Format} provenance must identify the provider");
        }

        ValidateContainer(first.Artifacts.Single(x => x.Format == "EPUB"), workspaceRoot, "mimetype", "OEBPS/content.opf");
        ValidateContainer(first.Artifacts.Single(x => x.Format == "DOCX"), workspaceRoot, "[Content_Types].xml", "word/document.xml");
        ValidateContainer(first.Artifacts.Single(x => x.Format == "KDP"), workspaceRoot, "metadata.json", "manuscript.txt");
        var pdf = Path.Combine(workspaceRoot, first.Artifacts.Single(x => x.Format == "PDF").RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Require((await File.ReadAllTextAsync(pdf)).StartsWith("%PDF-1.4", StringComparison.Ordinal), "PDF must contain a real PDF header");

        var second = await pipeline.ProduceAsync(request, provider.ProviderId, workspaceRoot);
        Require(second.Artifacts.Select(x => x.Sha256).SequenceEqual(first.Artifacts.Select(x => x.Sha256)), "restart replay must preserve exact artifact bytes");

        var overBudget = request with { MaximumCost = -1m };
        await RequireRejectedAsync(() => pipeline.ProduceAsync(overBudget, provider.ProviderId, workspaceRoot), "invalid budget must fail closed");
    }

    private static void ValidateContainer(DeepBookArtifact artifact, string root, params string[] expectedEntries)
    {
        var path = Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var archive = ZipFile.OpenRead(path);
        foreach (var entry in expectedEntries) Require(archive.GetEntry(entry) is not null, $"{artifact.Format} must contain {entry}");
    }

    private static async Task RequireRejectedAsync(Func<ValueTask<PublicationArtifactResult>> action, string message)
    {
        try { await action(); }
        catch (DeepBookProofValidationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
