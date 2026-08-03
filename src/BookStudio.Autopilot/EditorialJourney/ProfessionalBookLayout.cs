using System.Text.Json;

namespace BookStudio.Autopilot.EditorialJourney;

public enum PrintPaperType { White, Cream, Color }
public enum BindingDirection { LeftToRight, RightToLeft }

public sealed record ProfessionalLayoutRequest(
    string ProjectId,
    decimal TrimWidthInches,
    decimal TrimHeightInches,
    decimal BleedInches,
    decimal OutsideMarginInches,
    decimal TopMarginInches,
    decimal BottomMarginInches,
    int EstimatedPageCount,
    PrintPaperType PaperType,
    BindingDirection Binding,
    bool StartChaptersOnRecto,
    bool IncludeHeaders,
    bool IncludeFooters,
    bool IncludePageNumbers,
    string BodyFontFamily,
    decimal BodyFontSizePoints,
    decimal LineHeightMultiplier,
    int MinimumWidowOrphanLines)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BodyFontFamily);
        if (TrimWidthInches is < 4m or > 8.5m || TrimHeightInches is < 6m or > 11.69m) throw new ArgumentOutOfRangeException(nameof(TrimWidthInches));
        if (BleedInches is < 0m or > 0.25m) throw new ArgumentOutOfRangeException(nameof(BleedInches));
        if (OutsideMarginInches is < 0.25m or > 1.5m) throw new ArgumentOutOfRangeException(nameof(OutsideMarginInches));
        if (TopMarginInches is < 0.25m or > 1.5m || BottomMarginInches is < 0.25m or > 1.5m) throw new ArgumentOutOfRangeException("vertical margins");
        if (EstimatedPageCount is < 24 or > 828) throw new ArgumentOutOfRangeException(nameof(EstimatedPageCount));
        if (BodyFontSizePoints is < 8m or > 16m) throw new ArgumentOutOfRangeException(nameof(BodyFontSizePoints));
        if (LineHeightMultiplier is < 1m or > 2m) throw new ArgumentOutOfRangeException(nameof(LineHeightMultiplier));
        if (MinimumWidowOrphanLines is < 2 or > 5) throw new ArgumentOutOfRangeException(nameof(MinimumWidowOrphanLines));
    }
}

public sealed record CoverGeometry(
    decimal FrontWidthInches,
    decimal BackWidthInches,
    decimal SpineWidthInches,
    decimal TotalWidthInches,
    decimal TotalHeightInches,
    decimal BleedInches,
    int RequiredPixelWidth300Dpi,
    int RequiredPixelHeight300Dpi);

public sealed record InteriorLayoutRules(
    decimal GutterInches,
    decimal OutsideMarginInches,
    decimal TopMarginInches,
    decimal BottomMarginInches,
    bool RectoChapterStarts,
    bool MirrorMargins,
    bool SuppressHeadersOnChapterOpeners,
    bool SuppressPageNumberOnFrontMatter,
    int MinimumWidowOrphanLines,
    IReadOnlyList<string> FrontMatterOrder,
    IReadOnlyList<string> BackMatterOrder);

public sealed record ProfessionalLayoutManifest(
    string ProjectId,
    CoverGeometry Cover,
    InteriorLayoutRules Interior,
    IReadOnlyList<string> ValidationCodes,
    bool Passed,
    string Json);

public sealed class ProfessionalBookLayoutEngine
{
    public ProfessionalLayoutManifest Build(ProfessionalLayoutRequest request)
    {
        request.Validate();
        var codes = new List<string>();
        var gutter = RequiredGutter(request.EstimatedPageCount);
        if (request.OutsideMarginInches < 0.375m) codes.Add("outside_margin_too_small");
        if (request.TopMarginInches < 0.375m) codes.Add("top_margin_too_small");
        if (request.BottomMarginInches < 0.375m) codes.Add("bottom_margin_too_small");
        if (!request.StartChaptersOnRecto) codes.Add("chapter_recto_disabled");
        if (!request.IncludePageNumbers) codes.Add("page_numbers_disabled");
        if (request.LineHeightMultiplier < 1.15m) codes.Add("line_height_too_tight");

        var spine = CalculateSpine(request.EstimatedPageCount, request.PaperType);
        var totalWidth = request.TrimWidthInches * 2m + spine + request.BleedInches * 2m;
        var totalHeight = request.TrimHeightInches + request.BleedInches * 2m;
        var cover = new CoverGeometry(
            request.TrimWidthInches,
            request.TrimWidthInches,
            spine,
            decimal.Round(totalWidth, 4),
            decimal.Round(totalHeight, 4),
            request.BleedInches,
            (int)Math.Ceiling(totalWidth * 300m),
            (int)Math.Ceiling(totalHeight * 300m));
        var interior = new InteriorLayoutRules(
            gutter,
            request.OutsideMarginInches,
            request.TopMarginInches,
            request.BottomMarginInches,
            request.StartChaptersOnRecto,
            true,
            true,
            true,
            request.MinimumWidowOrphanLines,
            ["half-title", "title", "copyright", "dedication", "contents"],
            ["acknowledgements", "about-author"]);
        var payload = new { schemaVersion = 1, request.ProjectId, cover, interior, typography = new { request.BodyFontFamily, request.BodyFontSizePoints, request.LineHeightMultiplier } };
        return new ProfessionalLayoutManifest(request.ProjectId, cover, interior, codes, codes.Count == 0, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static decimal CalculateSpine(int pages, PrintPaperType paperType)
    {
        if (pages < 24) throw new ArgumentOutOfRangeException(nameof(pages));
        var perPage = paperType switch
        {
            PrintPaperType.White => 0.002252m,
            PrintPaperType.Cream => 0.0025m,
            PrintPaperType.Color => 0.002347m,
            _ => throw new ArgumentOutOfRangeException(nameof(paperType)),
        };
        return decimal.Round(pages * perPage, 4, MidpointRounding.AwayFromZero);
    }

    public static decimal RequiredGutter(int pages) => pages switch
    {
        <= 150 => 0.375m,
        <= 300 => 0.5m,
        <= 500 => 0.625m,
        <= 700 => 0.75m,
        _ => 0.875m,
    };
}
