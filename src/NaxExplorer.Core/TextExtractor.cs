using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;

namespace NaxExplorer.Core;

/// <summary>How the text of a file is read.</summary>
public enum TextFormat
{
    None,
    Plain,
    Html,
    Rtf,
    OfficeOpenXml,
    OpenDocument,
    Pdf,
}

/// <summary>
/// Reads the readable text of documents: plain text and source code, HTML, RTF, Word, Excel and PowerPoint files,
/// OpenDocument files and PDF. Files are opened for reading only and shared, so other programs can keep working with
/// them, and nothing is ever written.
/// </summary>
public static partial class TextExtractor
{
    /// <summary>Documents larger than this are not opened for their text.</summary>
    public const long MaxDocumentBytes = 200L * 1024 * 1024;

    public static TextFormat FormatOf(string extension)
    {
        extension = extension.ToLowerInvariant();
        return extension switch
        {
            ".docx" or ".docm" or ".dotx" or ".xlsx" or ".xlsm" or ".xltx" or ".pptx" or ".pptm" or ".ppsx" or ".potx" => TextFormat.OfficeOpenXml,
            ".odt" or ".ods" or ".odp" or ".ott" => TextFormat.OpenDocument,
            ".pdf" => TextFormat.Pdf,
            ".rtf" => TextFormat.Rtf,
            ".html" or ".htm" or ".xhtml" => TextFormat.Html,
            _ => FileTypes.IsKnownText(extension) ? TextFormat.Plain : TextFormat.None,
        };
    }

    /// <summary>
    /// The text of the file, at most <paramref name="maxChars"/> characters, or null when the file holds no readable
    /// text (binary data, an encrypted PDF, a damaged document). Unknown extensions are read as plain text only when
    /// <paramref name="sniffUnknown"/> is set and the first bytes look like text.
    /// </summary>
    public static string? Extract(string path, int maxChars, bool sniffUnknown = false, CancellationToken token = default)
    {
        TextFormat format = FormatOf(Path.GetExtension(path));
        if (format == TextFormat.None && !sniffUnknown) return null;
        try
        {
            using FileStream stream = TextSniffer.OpenShared(path);
            if (stream.Length > MaxDocumentBytes && format is not (TextFormat.Plain or TextFormat.None)) return null;
            return format switch
            {
                TextFormat.OfficeOpenXml or TextFormat.OpenDocument => ReadZippedXml(stream, format, maxChars, token),
                TextFormat.Pdf => ReadPdf(stream, maxChars, token),
                TextFormat.Rtf => Cut(RtfToText(ReadPlain(stream, maxChars * 4) ?? ""), maxChars),
                TextFormat.Html => Cut(HtmlToText(ReadPlain(stream, maxChars * 3) ?? ""), maxChars),
                _ => ReadPlain(stream, maxChars),
            };
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            // The document readers throw many kinds of exceptions on damaged or unusual files, including their own
            // types. Such a file simply has no readable text; it must never stop a search or the indexer.
            return null;
        }
    }

    private static string Cut(string text, int maxChars) => text.Length > maxChars ? text[..maxChars] : text;

    /// <summary>Reads a stream as text in its detected encoding, or returns null for binary data.</summary>
    internal static string? ReadPlain(Stream stream, int maxChars)
    {
        byte[] sample = new byte[TextSniffer.SampleSize];
        long start = stream.Position;
        int read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
        Encoding? encoding = TextSniffer.Detect(sample.AsSpan(0, read));
        if (encoding is null) return null;
        stream.Position = start;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        if (maxChars <= 0) return reader.ReadToEnd();
        char[] buffer = new char[maxChars];
        int chars = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, chars);
    }

    private static string ReadZippedXml(Stream stream, TextFormat format, int maxChars, CancellationToken token)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var parts = new List<ZipArchiveEntry>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (IsTextPart(name, format)) parts.Add(entry);
        }
        // Slides and sheets in their natural order: slide2 before slide10.
        parts.Sort((a, b) => NaturalComparer.Instance.Compare(a.FullName, b.FullName));
        var text = new StringBuilder();
        foreach (ZipArchiveEntry part in parts)
        {
            token.ThrowIfCancellationRequested();
            if (text.Length >= maxChars) break;
            using Stream partStream = part.Open();
            AppendXmlText(partStream, text, maxChars);
            text.Append('\n');
        }
        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }

    private static bool IsTextPart(string name, TextFormat format)
    {
        if (format == TextFormat.OpenDocument) return name is "content.xml" or "meta.xml";
        if (name.StartsWith("word/", StringComparison.Ordinal))
        {
            string file = name[5..];
            return file is "document.xml" or "footnotes.xml" or "endnotes.xml" or "comments.xml"
                   || (file.StartsWith("header", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal))
                   || (file.StartsWith("footer", StringComparison.Ordinal) && file.EndsWith(".xml", StringComparison.Ordinal));
        }
        if (name is "xl/sharedStrings.xml") return true;
        if (name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)) return true;
        if (name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)) return true;
        if (name.StartsWith("ppt/notesSlides/notesSlide", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal)) return true;
        return name is "docProps/core.xml";
    }

    /// <summary>Appends the text nodes of an XML part, with a line break after each paragraph-like element.</summary>
    private static void AppendXmlText(Stream xml, StringBuilder text, int maxChars)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            // Guards against parts that unpack to enormous sizes.
            MaxCharactersInDocument = Math.Max(1_000_000, (long)maxChars * 20),
        };
        using var reader = XmlReader.Create(xml, settings);
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                    text.Append(reader.Value);
                    break;
                case XmlNodeType.Element when reader.LocalName is "tab":
                    text.Append('\t');
                    break;
                case XmlNodeType.Element when reader.LocalName is "br" or "cr" && reader.IsEmptyElement:
                    text.Append('\n');
                    break;
                case XmlNodeType.Element when reader.LocalName is "s" && reader.IsEmptyElement:
                    text.Append(' ');
                    break;
                case XmlNodeType.EndElement when reader.LocalName is "p" or "h" or "si" or "row" or "tr" or "title" or "subject" or "description" or "keywords":
                    text.Append('\n');
                    break;
                case XmlNodeType.EndElement when reader.LocalName is "c" or "tc":
                    text.Append('\t');
                    break;
            }
            if (text.Length >= maxChars) return;
        }
    }

    private static string ReadPdf(Stream stream, int maxChars, CancellationToken token)
    {
        using PdfDocument document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        var text = new StringBuilder();
        for (int number = 1; number <= document.NumberOfPages; number++)
        {
            token.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            bool first = true;
            foreach (var word in page.GetWords())
            {
                if (!first) text.Append(' ');
                text.Append(word.Text);
                first = false;
            }
            text.Append('\n');
            if (text.Length >= maxChars) break;
        }
        return text.Length > maxChars ? text.ToString(0, maxChars) : text.ToString();
    }

    /// <summary>Removes scripts, styles and tags and decodes entities, keeping line breaks at block elements.</summary>
    public static string HtmlToText(string html)
    {
        string withoutCode = ScriptOrStyle().Replace(html, " ");
        string withBreaks = BlockTag().Replace(withoutCode, "\n");
        string withoutTags = AnyTag().Replace(withBreaks, "");
        return WebUtility.HtmlDecode(withoutTags);
    }

    /// <summary>A small RTF reader: keeps the text, turns \par into line breaks, decodes \'hh and \uN, drops hidden groups.</summary>
    public static string RtfToText(string rtf)
    {
        var text = new StringBuilder(rtf.Length / 2);
        var skipDepth = new Stack<bool>();
        bool skipping = false;
        int i = 0;
        int unicodeSkip = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '{')
            {
                skipDepth.Push(skipping);
                i++;
                // Groups that start with \* and a few known destinations hold no visible text.
                if (i + 1 < rtf.Length && rtf[i] == '\\' && rtf[i + 1] == '*') skipping = true;
                else if (StartsWithDestination(rtf, i)) skipping = true;
                continue;
            }
            if (c == '}')
            {
                skipping = skipDepth.Count > 0 && skipDepth.Pop();
                i++;
                continue;
            }
            if (c == '\\' && i + 1 < rtf.Length)
            {
                char next = rtf[i + 1];
                if (next is '\\' or '{' or '}')
                {
                    if (!skipping) text.Append(next);
                    i += 2;
                    continue;
                }
                if (next == '\'' && i + 3 < rtf.Length)
                {
                    if (unicodeSkip > 0) unicodeSkip--;
                    else if (!skipping && byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte value))
                        text.Append(TextSniffer.Legacy.GetString([value]));
                    i += 4;
                    continue;
                }
                int wordStart = i + 1;
                int j = wordStart;
                while (j < rtf.Length && char.IsAsciiLetter(rtf[j])) j++;
                string word = rtf[wordStart..j];
                int numberStart = j;
                if (j < rtf.Length && rtf[j] == '-') j++;
                while (j < rtf.Length && char.IsAsciiDigit(rtf[j])) j++;
                string number = rtf[numberStart..j];
                if (j < rtf.Length && rtf[j] == ' ') j++;
                i = j;
                if (skipping) continue;
                switch (word)
                {
                    case "par" or "line" or "row" or "sect" or "page":
                        text.Append('\n');
                        break;
                    case "tab" or "cell":
                        text.Append('\t');
                        break;
                    case "u" when int.TryParse(number, out int code):
                        text.Append((char)(code < 0 ? code + 65536 : code));
                        unicodeSkip = 1;
                        break;
                }
                if (word.Length == 0 && i == wordStart) i++;
                continue;
            }
            if (c is '\r' or '\n')
            {
                i++;
                continue;
            }
            if (unicodeSkip > 0)
            {
                unicodeSkip--;
                i++;
                continue;
            }
            if (!skipping) text.Append(c);
            i++;
        }
        return text.ToString();
    }

    private static bool StartsWithDestination(string rtf, int index)
    {
        foreach (string destination in (ReadOnlySpan<string>)["\\fonttbl", "\\colortbl", "\\stylesheet", "\\info", "\\pict", "\\header", "\\footer", "\\listtable", "\\listoverridetable", "\\rsidtbl", "\\generator", "\\themedata", "\\colorschememapping", "\\latentstyles", "\\datastore", "\\xmlnstbl"])
        {
            if (string.CompareOrdinal(rtf, index, destination, 0, destination.Length) == 0) return true;
        }
        return false;
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*/?\s*(p|div|br|li|tr|h[1-6]|table|section|article|header|footer)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex AnyTag();
}
