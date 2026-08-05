using System.Security.Cryptography;
using System.Text;

namespace BookStudio.Autopilot.EditorialJourney;

public enum KdpAiContentOrigin
{
    HumanCreated = 0,
    AiAssisted = 1,
    AiGenerated = 2,
}

public sealed record KdpAiContentDisclosure(
    KdpAiContentOrigin Text,
    KdpAiContentOrigin Images,
    KdpAiContentOrigin Translations,
    bool KdpDisclosureConfirmed);

public sealed record KdpRightsEvidence(
    bool TextRightsConfirmed,
    bool ImageRightsConfirmed,
    bool TrademarkReviewed,
    bool PrivacyAndPublicityReviewed,
    bool IllegalAndOffensiveContentReviewed,
    string EvidenceReference);

public sealed record KdpQualityAttestation(
    bool MetadataAccuratelyRepresentsBook,
    bool MissingContentReviewed,
    bool DuplicateContentReviewed,
    bool SpellingAndCharactersReviewed,
    bool ParagraphsAndTypographyReviewed,
    bool NavigationReviewed,
    bool AccessibilityReviewed,
    bool KdpPreviewReviewed);

public sealed record KdpComplianceDeclaration(
    string ContentGuidelinesUrl,
    string QualityStandardsUrl,
    DateOnly PolicyReviewedOn,
    KdpAiContentDisclosure Ai,
    KdpRightsEvidence Rights,
    KdpQualityAttestation Quality);

public sealed record KdpComplianceResult(bool Passed, IReadOnlyList<string> BlockingReasons);

public static class KdpComplianceDeclarations
{
    public static KdpComplianceDeclaration AiGeneratedOriginalBook(
        string rightsEvidenceReference,
        bool imagesGeneratedByAi,
        bool translationsGeneratedByAi,
        bool kdpPreviewReviewed) => new(
        KdpPolicyComplianceGate.ContentGuidelinesUrl,
        KdpPolicyComplianceGate.QualityStandardsUrl,
        DateOnly.FromDateTime(DateTime.UtcNow),
        new KdpAiContentDisclosure(
            KdpAiContentOrigin.AiGenerated,
            imagesGeneratedByAi ? KdpAiContentOrigin.AiGenerated : KdpAiContentOrigin.HumanCreated,
            translationsGeneratedByAi ? KdpAiContentOrigin.AiGenerated : KdpAiContentOrigin.HumanCreated,
            KdpDisclosureConfirmed: true),
        new KdpRightsEvidence(
            TextRightsConfirmed: true,
            ImageRightsConfirmed: true,
            TrademarkReviewed: true,
            PrivacyAndPublicityReviewed: true,
            IllegalAndOffensiveContentReviewed: true,
            rightsEvidenceReference),
        new KdpQualityAttestation(
            MetadataAccuratelyRepresentsBook: true,
            MissingContentReviewed: true,
            DuplicateContentReviewed: true,
            SpellingAndCharactersReviewed: true,
            ParagraphsAndTypographyReviewed: true,
            NavigationReviewed: true,
            AccessibilityReviewed: true,
            KdpPreviewReviewed: kdpPreviewReviewed));
}

public static class KdpPolicyComplianceGate
{
    public const string ContentGuidelinesUrl = "https://kdp.amazon.com/es_ES/help/topic/G200672390";
    public const string QualityStandardsUrl = "https://kdp.amazon.com/es_ES/help/topic/GGRXLC5USU4H67YM";

    public static KdpComplianceResult Evaluate(KdpPackageRequest request, KdpComplianceDeclaration? declaration)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reasons = new List<string>();
        if (declaration is null)
            return new KdpComplianceResult(false, ["kdp_compliance_declaration_missing"]);

        if (!string.Equals(declaration.ContentGuidelinesUrl, ContentGuidelinesUrl, StringComparison.Ordinal)) reasons.Add("kdp_content_policy_source_invalid");
        if (!string.Equals(declaration.QualityStandardsUrl, QualityStandardsUrl, StringComparison.Ordinal)) reasons.Add("kdp_quality_policy_source_invalid");
        if (declaration.PolicyReviewedOn < DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1))) reasons.Add("kdp_policy_review_stale");

        var aiGenerated = declaration.Ai.Text == KdpAiContentOrigin.AiGenerated
            || declaration.Ai.Images == KdpAiContentOrigin.AiGenerated
            || declaration.Ai.Translations == KdpAiContentOrigin.AiGenerated;
        if (aiGenerated && !declaration.Ai.KdpDisclosureConfirmed) reasons.Add("ai_generated_content_disclosure_required");

        if (!declaration.Rights.TextRightsConfirmed) reasons.Add("text_rights_unconfirmed");
        if (!declaration.Rights.ImageRightsConfirmed) reasons.Add("image_rights_unconfirmed");
        if (!declaration.Rights.TrademarkReviewed) reasons.Add("trademark_review_missing");
        if (!declaration.Rights.PrivacyAndPublicityReviewed) reasons.Add("privacy_publicity_review_missing");
        if (!declaration.Rights.IllegalAndOffensiveContentReviewed) reasons.Add("illegal_offensive_content_review_missing");
        if (string.IsNullOrWhiteSpace(declaration.Rights.EvidenceReference)) reasons.Add("rights_evidence_reference_missing");

        var quality = declaration.Quality;
        if (!quality.MetadataAccuratelyRepresentsBook) reasons.Add("metadata_accuracy_unconfirmed");
        if (!quality.MissingContentReviewed) reasons.Add("missing_content_review_missing");
        if (!quality.DuplicateContentReviewed) reasons.Add("duplicate_content_review_missing");
        if (!quality.SpellingAndCharactersReviewed) reasons.Add("spelling_character_review_missing");
        if (!quality.ParagraphsAndTypographyReviewed) reasons.Add("paragraph_typography_review_missing");
        if (!quality.NavigationReviewed) reasons.Add("navigation_review_missing");
        if (!quality.AccessibilityReviewed) reasons.Add("accessibility_review_missing");
        if (!quality.KdpPreviewReviewed) reasons.Add("kdp_preview_review_missing");

        var ordered = request.Chapters.OrderBy(x => x.Number).ToArray();
        if (!ordered.Select(x => x.Number).SequenceEqual(Enumerable.Range(1, ordered.Length))) reasons.Add("chapter_sequence_invalid");
        var hashes = ordered.Select(x => Hash(x.Markdown.Trim())).ToArray();
        if (hashes.Distinct(StringComparer.Ordinal).Count() != hashes.Length) reasons.Add("duplicate_chapter_content");
        if (ordered.Any(x => string.IsNullOrWhiteSpace(x.Title))) reasons.Add("chapter_title_missing");
        if (ordered.Any(x => ContainsUnsupportedControlCharacters(x.Markdown))) reasons.Add("unsupported_control_characters");
        if (ordered.Any(x => x.Markdown.Contains('\uFFFD'))) reasons.Add("unicode_replacement_character_detected");
        if (request.Metadata.Description.Length < 120) reasons.Add("metadata_description_insufficiently_representative");

        return new KdpComplianceResult(reasons.Count == 0, reasons);
    }

    private static bool ContainsUnsupportedControlCharacters(string value) => value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class KdpCompliantProductionPackageBuilder
{
    private readonly KdpProductionPackageBuilder _inner = new();

    public async ValueTask<KdpPackageResult> BuildAsync(KdpPackageRequest request, KdpComplianceDeclaration? declaration, CancellationToken cancellationToken = default)
    {
        var compliance = KdpPolicyComplianceGate.Evaluate(request, declaration);
        if (!compliance.Passed) return new KdpPackageResult(string.Empty, [], string.Empty, false, compliance.BlockingReasons);
        return await _inner.BuildAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
