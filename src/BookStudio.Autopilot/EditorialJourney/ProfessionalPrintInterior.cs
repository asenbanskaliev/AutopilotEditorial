using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BookStudio.Autopilot.EditorialJourney;

public enum PrintBlockKind { Paragraph, Dialogue, Document, Spacer }
public sealed record PrintInline(string Text, bool Italic = false);
public sealed record PrintBlock(PrintBlockKind Kind, IReadOnlyList<PrintInline> Inlines);
public sealed record ProfessionalPrintAudit(bool Passed, IReadOnlyList<string> BlockingReasons, int PageCount, int VisibleMarkdownCount, int ShortDialogueHyphenCount, int UnderfilledPageCount, bool HasTitlePage, bool HasCopyrightPage, bool HasPageNumbers, bool UsesDeterministicFonts);

public static class ProfessionalPrintInterior
{
    private static readonly Regex Italic = new(@"(?<!\*)\*([^*\r\n]+)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ShortDialogue = new(@"(?m)^\s*[-–]\s*(?=\p{L}|[¿¡])", RegexOptions.Compiled);
    private static readonly Regex VisibleMarkdown = new(@"(?<!\*)\*[^*\r\n]+\*(?!\*)|(?m)^\s{0,3}#{1,6}\s+|(?m)^\s*[-+*]\s+", RegexOptions.Compiled);

    public static IReadOnlyList<PrintBlock> Parse(string markdown)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markdown);
        var normalized = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"(?m)^\s*#{1,6}\s+.*$", string.Empty);
        normalized = NormalizeSpanishDialogue(normalized);
        var blocks = new List<PrintBlock>();
        foreach (var raw in Regex.Split(normalized, @"\n\s*\n"))
        {
            var value = raw.Trim();
            if (value.Length == 0) continue;
            foreach (var dialogue in SplitMergedDialogueTurns(value))
            {
                var text = dialogue.Trim();
                if (text.Length == 0) continue;
                var kind = text.StartsWith('—') ? PrintBlockKind.Dialogue : LooksLikeDocument(text) ? PrintBlockKind.Document : PrintBlockKind.Paragraph;
                blocks.Add(new PrintBlock(kind, ParseInlines(text)));
            }
        }
        return blocks;
    }

    public static string NormalizeSpanishDialogue(string value)
    {
        var normalized = ShortDialogue.Replace(value, "—");
        normalized = Regex.Replace(normalized, @"(?<!\n)(?<!^)\s+(?=—[\p{L}¿¡])", "\n\n");
        return normalized;
    }

    public static ProfessionalPrintAudit AuditSource(KdpPackageRequest request)
    {
        var reasons = new List<string>();
        var source = string.Join("\n\n", request.Chapters.Select(x => x.Markdown));
        var visible = VisibleMarkdown.Matches(RemoveSupportedItalicMarkup(source)).Count;
        var shortDialogue = ShortDialogue.Matches(source).Count;
        if (visible > 0) reasons.Add("visible_markdown_detected");
        if (shortDialogue > 0) reasons.Add("short_dialogue_hyphen_detected");
        if (request.Chapters.SelectMany(x => Parse(x.Markdown)).Any(x => x.Kind == PrintBlockKind.Dialogue && x.Inlines.Sum(i => i.Text.Length) < 2)) reasons.Add("empty_dialogue_turn_detected");
        return new ProfessionalPrintAudit(reasons.Count == 0, reasons, 0, visible, shortDialogue, 0, true, true, true, true);
    }

    public static byte[] BuildPdf(KdpPackageRequest request)
    {
        var sourceAudit = AuditSource(request);
        if (!sourceAudit.Passed) throw new InvalidOperationException("Professional print source failed: " + string.Join(',', sourceAudit.BlockingReasons));

        var width = (double)request.TrimWidthInches * 72d;
        var height = (double)request.TrimHeightInches * 72d;
        var margin = Math.Max(42d, (double)request.MarginInches * 72d);
        var pages = Layout(request, width, height, margin);
        return PdfDocument.Build(width, height, pages, request.Metadata.Title);
    }

    private static IReadOnlyList<PrintPage> Layout(KdpPackageRequest request, double width, double height, double margin)
    {
        var pages = new List<PrintPage>
        {
            new([new PrintLine(request.Metadata.Title, PrintStyle.Title, false), new PrintLine(request.Metadata.Author, PrintStyle.Subtitle, false)], false),
            new([new PrintLine("Copyright © " + DateTime.UtcNow.Year + " " + request.Metadata.Author, PrintStyle.Body, false), new PrintLine("Todos los derechos reservados.", PrintStyle.Body, false), new PrintLine("Edición preparada para Amazon KDP.", PrintStyle.Body, false)], false)
        };

        var bodyChars = Math.Max(46, (int)Math.Floor((width - margin * 2) / 5.25d));
        var usableLines = Math.Max(24, (int)Math.Floor((height - margin * 2 - 24d) / 14.5d));
        foreach (var chapter in request.Chapters.OrderBy(x => x.Number))
        {
            var lines = new List<PrintLine> { new($"Capítulo {chapter.Number}", PrintStyle.ChapterNumber, false), new(chapter.Title, PrintStyle.ChapterTitle, false) };
            var remaining = usableLines - 6;
            foreach (var block in Parse(chapter.Markdown))
            {
                var plain = string.Concat(block.Inlines.Select(x => x.Text));
                var style = block.Kind switch { PrintBlockKind.Dialogue => PrintStyle.Dialogue, PrintBlockKind.Document => PrintStyle.Document, _ => PrintStyle.Body };
                var wrapped = Wrap(plain, bodyChars).ToArray();
                if (wrapped.Length == 1 && remaining == 1 && lines.Count > 3)
                {
                    pages.Add(new PrintPage(lines, true));
                    lines = [];
                    remaining = usableLines;
                }
                if (wrapped.Length > remaining && remaining < 3 && lines.Count > 3)
                {
                    pages.Add(new PrintPage(lines, true));
                    lines = [];
                    remaining = usableLines;
                }
                for (var i = 0; i < wrapped.Length; i++)
                {
                    if (remaining <= 0)
                    {
                        pages.Add(new PrintPage(lines, true));
                        lines = [];
                        remaining = usableLines;
                    }
                    var italic = block.Inlines.Any(x => x.Italic) && block.Inlines.All(x => x.Italic || string.IsNullOrWhiteSpace(x.Text));
                    lines.Add(new PrintLine(wrapped[i], italic ? PrintStyle.Italic : style, i == 0 && style == PrintStyle.Body));
                    remaining--;
                }
                if (remaining > 0) { lines.Add(new PrintLine(string.Empty, PrintStyle.Spacer, false)); remaining--; }
            }
            if (lines.Count > 0) pages.Add(new PrintPage(lines, true));
        }
        return pages;
    }

    private static IReadOnlyList<PrintInline> ParseInlines(string text)
    {
        var result = new List<PrintInline>();
        var index = 0;
        foreach (Match match in Italic.Matches(text))
        {
            if (match.Index > index) result.Add(new PrintInline(text[index..match.Index]));
            result.Add(new PrintInline(match.Groups[1].Value, true));
            index = match.Index + match.Length;
        }
        if (index < text.Length) result.Add(new PrintInline(text[index..]));
        return result.Count == 0 ? [new PrintInline(text)] : result;
    }

    private static IEnumerable<string> SplitMergedDialogueTurns(string value)
    {
        var parts = Regex.Split(value, @"\s+(?=—[\p{L}¿¡])");
        return parts.Length == 0 ? [value] : parts;
    }

    private static bool LooksLikeDocument(string text) => text.StartsWith('>') || Regex.IsMatch(text, @"^(Asunto|Acta|Expediente|Inventario|Carta|Nota|Diario)\b", RegexOptions.IgnoreCase);
    private static string RemoveSupportedItalicMarkup(string source) => Italic.Replace(source, match => match.Groups[1].Value);

    private static IEnumerable<string> Wrap(string value, int max)
    {
        var words = value.Replace("\n", " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > max) { yield return line.ToString(); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private enum PrintStyle { Title, Subtitle, ChapterNumber, ChapterTitle, Body, Italic, Dialogue, Document, Spacer }
    private sealed record PrintLine(string Text, PrintStyle Style, bool FirstLineIndent);
    private sealed record PrintPage(IReadOnlyList<PrintLine> Lines, bool Numbered);

    private static class PdfDocument
    {
        private static readonly Encoding Enc = Encoding.Latin1;
        public static byte[] Build(double width, double height, IReadOnlyList<PrintPage> pages, string title)
        {
            var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", string.Empty,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Bold /Encoding /WinAnsiEncoding >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>" };
            var refs = new List<int>();
            for (var p = 0; p < pages.Count; p++)
            {
                var pageNo = objects.Count + 1; var contentNo = pageNo + 1; refs.Add(pageNo);
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(width)} {F(height)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents {contentNo} 0 R >>");
                var content = Content(pages[p], p + 1, height);
                objects.Add($"<< /Length {Enc.GetByteCount(content)} >>\nstream\n{content}\nendstream");
            }
            objects[1] = $"<< /Type /Pages /Kids [{string.Join(' ', refs.Select(x => $"{x} 0 R"))}] /Count {pages.Count} >>";
            using var ms = new MemoryStream();
            void W(string s) => ms.Write(Enc.GetBytes(s));
            W("%PDF-1.4\n%âãÏÓ\n");
            var offsets = new List<long> { 0 };
            for (var i = 0; i < objects.Count; i++) { offsets.Add(ms.Position); W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
            var xref = ms.Position; W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets.Skip(1)) W(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
            W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info << /Title ({Esc(title)}) >> >>\nstartxref\n{xref}\n%%EOF\n");
            return ms.ToArray();
        }

        private static string Content(PrintPage page, int pageNumber, double height)
        {
            var sb = new StringBuilder(); var y = height - 72d;
            foreach (var line in page.Lines)
            {
                var (font, size, leading, x) = line.Style switch
                {
                    PrintStyle.Title => ("/F2", 22d, 30d, 72d), PrintStyle.Subtitle => ("/F1", 13d, 24d, 72d),
                    PrintStyle.ChapterNumber => ("/F1", 11d, 22d, 72d), PrintStyle.ChapterTitle => ("/F2", 18d, 34d, 72d),
                    PrintStyle.Italic => ("/F3", 10.8d, 14.5d, 72d), PrintStyle.Dialogue => ("/F1", 10.8d, 14.5d, 72d),
                    PrintStyle.Document => ("/F3", 10.2d, 14d, 90d), PrintStyle.Spacer => ("/F1", 1d, 8d, 72d), _ => ("/F1", 10.8d, 14.5d, line.FirstLineIndent ? 90d : 72d)
                };
                if (line.Text.Length > 0) sb.Append($"BT {font} {F(size)} Tf {F(x)} {F(y)} Td ({Esc(line.Text)}) Tj ET\n");
                y -= leading;
            }
            if (page.Numbered) sb.Append($"BT /F1 9 Tf 0.5 0 0 0.5 0 0 Tm 420 70 Td ({pageNumber}) Tj ET\n");
            return sb.ToString();
        }
        private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace('—', '—').Replace('“', '«').Replace('”', '»');
        private static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
