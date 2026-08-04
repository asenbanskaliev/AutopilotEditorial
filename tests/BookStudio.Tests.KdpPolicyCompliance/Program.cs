using BookStudio.Autopilot.EditorialJourney;

var chapter1 = "# Capítulo 1: La señal\n\n" + string.Join(' ', Enumerable.Repeat("Ángela revisa el archivo, comprueba la cronología y conserva una pista original para proteger la memoria pública.", 45));
var chapter2 = "# Capítulo 2: La ausencia\n\n" + string.Join(' ', Enumerable.Repeat("La investigación avanza en Pamplona con escenas distintas, diálogo, conflicto y una decisión irreversible para la protagonista.", 45));
var request = new KdpPackageRequest(
    "kdp-policy-book",
    Path.Combine(Path.GetTempPath(), "kdp-policy-" + Guid.NewGuid().ToString("N")),
    6m,
    9m,
    0.5m,
    new KdpMetadata(
        "El archivo de las ausencias",
        "Asen Bansk",
        "es-ES",
        "Una archivera municipal de Pamplona descubre expedientes eliminados que anticipan desapariciones y debe decidir si protege la memoria privada o revela una conspiración pública.",
        ["FICTION / Mystery & Detective / General"],
        ["misterio", "Pamplona", "archivo", "memoria"]),
    [new KdpChapter(1, "La señal", chapter1), new KdpChapter(2, "La ausencia", chapter2)],
    new KdpCoverInput(1800, 2700, 300, "image/jpeg", new string('a', 64)));

var missing = KdpPolicyComplianceGate.Evaluate(request, null);
Require(!missing.Passed && missing.BlockingReasons.Contains("kdp_compliance_declaration_missing"), "missing declaration was accepted");

var undisclosed = KdpComplianceDeclarations.AiGeneratedOriginalBook("evidence/run-001.json", false, false, true) with
{
    Ai = new KdpAiContentDisclosure(KdpAiContentOrigin.AiGenerated, KdpAiContentOrigin.HumanCreated, KdpAiContentOrigin.HumanCreated, false),
};
var undisclosedResult = KdpPolicyComplianceGate.Evaluate(request, undisclosed);
Require(!undisclosedResult.Passed && undisclosedResult.BlockingReasons.Contains("ai_generated_content_disclosure_required"), "undisclosed AI content was accepted");

var noPreview = KdpComplianceDeclarations.AiGeneratedOriginalBook("evidence/run-001.json", false, false, false);
var noPreviewResult = KdpPolicyComplianceGate.Evaluate(request, noPreview);
Require(!noPreviewResult.Passed && noPreviewResult.BlockingReasons.Contains("kdp_preview_review_missing"), "missing preview review was accepted");

var duplicateRequest = request with { Chapters = [request.Chapters[0], request.Chapters[0] with { Number = 2, Title = "Duplicado" }] };
var duplicateResult = KdpPolicyComplianceGate.Evaluate(duplicateRequest, KdpComplianceDeclarations.AiGeneratedOriginalBook("evidence/run-001.json", false, false, true));
Require(!duplicateResult.Passed && duplicateResult.BlockingReasons.Contains("duplicate_chapter_content"), "duplicate chapter was accepted");

var compliant = KdpComplianceDeclarations.AiGeneratedOriginalBook("evidence/run-001.json", false, false, true);
var passed = KdpPolicyComplianceGate.Evaluate(request, compliant);
Require(passed.Passed, string.Join(',', passed.BlockingReasons));

var guarded = await new KdpCompliantProductionPackageBuilder().BuildAsync(request, null);
Require(!guarded.Passed && guarded.BlockingReasons.Contains("kdp_compliance_declaration_missing"), "guarded builder did not fail closed");

Console.WriteLine("PASS KDP official content and quality policy enforcement");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
