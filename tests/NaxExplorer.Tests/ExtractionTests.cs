using NaxExplorer.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace NaxExplorer.Tests;

internal static class ExtractionTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("word_document_text_is_read_paragraph_by_paragraph", "The text of a .docx file is read with a line break after each paragraph.", () =>
        {
            string folder = Fixtures.TempFolder("docx");
            string path = Fixtures.Docx(folder, "plan.docx", "Oracle 19c Upgrade Plan", "Run preupgrade.jar first.");
            string? text = TextExtractor.Extract(path, 10_000);
            Check.That(text is not null && text.Contains("Oracle 19c Upgrade Plan\nRun preupgrade.jar first."), $"unexpected text: {text}");
        });

        yield return ("excel_cells_and_powerpoint_slides_are_read_in_order", "Shared strings of a workbook and slides 1, 2 and 10 of a presentation are read, slides in their natural order.", () =>
        {
            string folder = Fixtures.TempFolder("office");
            string xlsx = Fixtures.Zip(folder, "budget.xlsx", ("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Licence costs</t></si><si><t>Support</t></si></sst>"));
            Check.That(TextExtractor.Extract(xlsx, 10_000)!.Contains("Licence costs\nSupport"), "workbook strings missing");
            string Slide(string text) => $"<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";
            string pptx = Fixtures.Zip(folder, "deck.pptx", ("ppt/slides/slide10.xml", Slide("Ten")), ("ppt/slides/slide2.xml", Slide("Two")), ("ppt/slides/slide1.xml", Slide("One")));
            string text = TextExtractor.Extract(pptx, 10_000)!;
            Check.That(text.IndexOf("One") < text.IndexOf("Two") && text.IndexOf("Two") < text.IndexOf("Ten"), $"slide order wrong: {text}");
        });

        yield return ("pdf_text_is_read", "The words on the pages of a PDF are read.", () =>
        {
            string folder = Fixtures.TempFolder("pdf");
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText("Datenbank Migration auf 23ai", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
            string path = Path.Combine(folder, "migration.pdf");
            File.WriteAllBytes(path, builder.Build());
            string? text = TextExtractor.Extract(path, 10_000);
            Check.That(text is not null && text.Contains("Migration auf 23ai"), $"unexpected text: {text}");
        });

        yield return ("rtf_text_keeps_umlauts_and_drops_formatting", "RTF control words and the font table are removed, and escaped umlauts are decoded.", () =>
        {
            // A regular string with doubled backslashes, so the compiler never reads 荤 as a character escape.
            string rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0 Gr\\'fc\\'dfe aus K\\'f6ln\\par Zweite Zeile \\u8364? Euro}";
            string text = TextExtractor.RtfToText(rtf);
            Check.That(text.Contains("Grüße aus Köln") && text.Contains("\nZweite Zeile € Euro") && !text.Contains("Arial"), $"unexpected text: {text}");
        });

        yield return ("html_text_drops_scripts_styles_and_tags", "Scripts, styles and tags are removed from HTML and entities are decoded.", () =>
        {
            string text = TextExtractor.HtmlToText("<html><style>p{color:red}</style><script>var x=1;</script><p>Fish &amp; Chips</p></html>");
            Check.That(text.Contains("Fish & Chips") && !text.Contains("color") && !text.Contains("var x"), $"unexpected text: {text}");
        });

        yield return ("damaged_documents_have_no_text_instead_of_failing", "A .docx or .pdf that is not a valid file gives no text and no error.", () =>
        {
            string folder = Fixtures.TempFolder("broken");
            string docx = Fixtures.Write(folder, "broken.docx", "this is not a zip file");
            string pdf = Fixtures.Write(folder, "broken.pdf", "%PDF-1.7 garbage");
            Check.Equal(null, TextExtractor.Extract(docx, 1000));
            Check.Equal(null, TextExtractor.Extract(pdf, 1000));
        });
    }
}
