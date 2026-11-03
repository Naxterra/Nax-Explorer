using System.Text;

namespace NaxExplorer.Search;

/// <summary>
/// Cuts document text into overlapping pieces of about a thousand characters, the size the model reads well. Pieces
/// end at paragraph breaks where possible, then at sentence ends, then at spaces.
/// </summary>
public static class Chunker
{
    public const int TargetChars = 1000;
    public const int OverlapChars = 150;
    public const int MaxChunks = 400;

    public static List<string> Split(string text, int target = TargetChars, int overlap = OverlapChars, int maxChunks = MaxChunks)
    {
        string clean = Clean(text);
        var chunks = new List<string>();
        int position = 0;
        while (position < clean.Length && chunks.Count < maxChunks)
        {
            int remaining = clean.Length - position;
            if (remaining <= target + target / 4)
            {
                AddChunk(clean[position..]);
                break;
            }
            int end = FindBreak(clean, position, target);
            AddChunk(clean[position..end]);
            // The next piece repeats the end of this one, so a sentence cut in two is still whole in one of them.
            int next = end - overlap;
            if (next <= position) next = end;
            int space = clean.IndexOf(' ', next);
            if (space > 0 && space < end) next = space + 1;
            position = next;
        }
        return chunks;

        void AddChunk(string piece)
        {
            piece = piece.Trim();
            if (piece.Length > 0) chunks.Add(piece);
        }
    }

    private static int FindBreak(string text, int start, int target)
    {
        int ideal = start + target;
        int lowest = start + target / 2;
        int paragraph = text.LastIndexOf("\n\n", ideal, ideal - lowest, StringComparison.Ordinal);
        if (paragraph > lowest) return paragraph + 2;
        int line = text.LastIndexOf('\n', ideal, ideal - lowest);
        if (line > lowest) return line + 1;
        for (int i = ideal; i > lowest; i--)
        {
            if (text[i] is '.' or '!' or '?' or ';' && i + 1 < text.Length && text[i + 1] == ' ') return i + 1;
        }
        int space = text.LastIndexOf(' ', ideal, ideal - lowest);
        return space > lowest ? space + 1 : ideal;
    }

    /// <summary>Joins runs of spaces and tabs, removes control characters and keeps at most one empty line in a row.</summary>
    internal static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        int newlines = 0;
        bool space = false;
        foreach (char raw in text)
        {
            char c = raw == '\r' ? '\n' : raw;
            if (c == '\n')
            {
                if (raw == '\r') continue;
                newlines++;
                space = false;
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = true;
                continue;
            }
            if (newlines > 0)
            {
                if (builder.Length > 0) builder.Append(newlines > 1 ? "\n\n" : "\n");
                newlines = 0;
                space = false;
            }
            else if (space)
            {
                if (builder.Length > 0) builder.Append(' ');
                space = false;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }
}
