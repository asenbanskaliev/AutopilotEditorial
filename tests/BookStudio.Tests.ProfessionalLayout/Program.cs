using BookStudio.Autopilot.EditorialJourney;

var engine = new ProfessionalBookLayoutEngine();
var request = new ProfessionalLayoutRequest("vs140", 6m, 9m, 0.125m, 0.5m, 0.5m, 0.5m, 320, PrintPaperType.Cream, BindingDirection.LeftToRight, true, true, true, true, "Literata", 11m, 1.3m, 2);
var result = engine.Build(request);
Require(result.Passed, string.Join(',', result.ValidationCodes));
Require(result.Cover.SpineWidthInches == 0.8m, "cream spine calculation mismatch");
Require(result.Cover.TotalWidthInches == 13.05m && result.Cover.TotalHeightInches == 9.25m, "cover geometry mismatch");
Require(result.Cover.RequiredPixelWidth300Dpi == 3915 && result.Cover.RequiredPixelHeight300Dpi == 2775, "300 DPI canvas mismatch");
Require(result.Interior.GutterInches == 0.625m && result.Interior.MirrorMargins, "gutter or mirror margins mismatch");
Require(result.Interior.SuppressHeadersOnChapterOpeners && result.Interior.SuppressPageNumberOnFrontMatter, "professional suppression rules missing");
Require(result.Interior.FrontMatterOrder.SequenceEqual(["half-title", "title", "copyright", "dedication", "contents"]), "front matter order mismatch");

var invalid = engine.Build(request with { OutsideMarginInches = 0.3m, StartChaptersOnRecto = false, IncludePageNumbers = false, LineHeightMultiplier = 1.05m });
Require(!invalid.Passed, "invalid layout passed");
foreach (var code in new[] { "outside_margin_too_small", "chapter_recto_disabled", "page_numbers_disabled", "line_height_too_tight" }) Require(invalid.ValidationCodes.Contains(code), $"missing {code}");
Require(ProfessionalBookLayoutEngine.CalculateSpine(200, PrintPaperType.White) == 0.4504m, "white paper spine mismatch");
Console.WriteLine("PASS VS-140 professional cover and interior layout");

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
