using System.Globalization;
using System.Text;
using NaxExplorer.Core;

namespace NaxExplorer.Search;

/// <summary>Filename syntax stays literal; natural-language searches can also use document meaning.</summary>
internal sealed class SearchQuery
{
    private readonly NameMatcher? _pattern;
    private readonly string[] _nameTerms;
    private readonly string[] _contextTerms;
    public IReadOnlyList<string> Terms { get; }
    public string Phrase { get; }
    public bool IsPattern { get; }
    public bool Literal { get; }
    public bool Identifier { get; }
    public bool IsEmpty => !IsPattern && _nameTerms.Length == 0;

    public SearchQuery(string text)
    {
        Terms = QueryTerms.Split(text);
        string plain = text.Replace("\"", "").Trim();
        IsPattern = plain.Contains('*') || plain.Contains('?') && !plain.Any(char.IsWhiteSpace)
            && (plain.Contains('.') || plain.All(c => c == '?'));
        if (IsPattern) _pattern = new NameMatcher(plain);
        Literal = IsPattern || text.Contains('"');
        Identifier = Terms.Count == 1 && (plain.Any(char.IsDigit) || plain.IndexOfAny(['\\', '/', '@']) >= 0);
        _nameTerms = Terms.Select(NormalizeName).Where(t => t.Length > 0).ToArray();
        Phrase = NormalizeName(plain);
        _contextTerms = Phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 2 && !ContextStopWords.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool MatchesName(string name)
    {
        if (_pattern is not null) return _pattern.Matches(name);
        if (_nameTerms.Length == 0) return false;
        string normalized = NormalizeName(name);
        return _nameTerms.All(term => normalized.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public int NameTier(string name)
    {
        if (NormalizeName(name).Equals(Phrase, StringComparison.OrdinalIgnoreCase)) return 0;
        string stem = NormalizeName(Path.GetFileNameWithoutExtension(name));
        if (stem.Equals(Phrase, StringComparison.OrdinalIgnoreCase)) return 1;
        return stem.Contains(Phrase, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    /// <summary>Partial filename evidence supports a semantic match, without claiming that every query word matched.</summary>
    public float NameSupport(string name)
    {
        if (_contextTerms.Length == 0) return 0;
        string[] words = NormalizeName(Path.GetFileNameWithoutExtension(name)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matched = _contextTerms.Count(term => words.Any(word => word.StartsWith(term, StringComparison.OrdinalIgnoreCase)));
        return (float)matched / _contextTerms.Length;
    }

    private static readonly HashSet<string> ContextStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "are", "was", "were", "what", "where", "which", "find", "show", "please",
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "eines", "und", "oder", "ist", "sind", "mit", "von", "fur", "welche", "suche", "finde", "zeige", "mir", "mein", "meine", "bitte",
    };

    /// <summary>Compare composed and decomposed accents, apostrophes and filename separators consistently.</summary>
    public static string NormalizeName(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (char c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c is '\'' or '\u2018' or '\u2019' or '\u02BC') continue;
            if (char.IsLetterOrDigit(c)) result.Append(c);
            else if (result.Length > 0 && result[^1] != ' ') result.Append(' ');
        }
        return result.ToString().Trim();
    }
}
