using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;
using System.Threading.Channels;

namespace NaxExplorer.Core;

/// <summary>Splits a search text into words, keeping "quoted phrases" together.</summary>
public static class QueryTerms
{
    public static List<string> Split(string query)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        foreach (char c in query)
        {
            if (c == '"')
            {
                Flush();
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
                continue;
            }
            current.Append(c);
        }
        Flush();
        return terms;

        void Flush()
        {
            string term = current.ToString().Trim();
            if (term.Length > 0 && !terms.Contains(term, StringComparer.OrdinalIgnoreCase)) terms.Add(term);
            current.Clear();
        }
    }
}

/// <summary>
/// Matches file names. Text with * or ? is a wildcard pattern (several patterns may be separated by ; or spaces, and any
/// of them may match). Other text matches names that contain every word, in any order, ignoring case.
/// </summary>
public sealed class NameMatcher
{
    private readonly string[] _patterns;
    private readonly string[] _terms;

    public NameMatcher(string query)
    {
        if (query.IndexOfAny(['*', '?']) >= 0)
        {
            _patterns = query.Split([';', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _terms = [];
        }
        else
        {
            _patterns = [];
            _terms = [.. QueryTerms.Split(query)];
        }
    }

    public bool IsEmpty => _patterns.Length == 0 && _terms.Length == 0;

    public bool Matches(ReadOnlySpan<char> name)
    {
        if (_patterns.Length > 0)
        {
            foreach (string pattern in _patterns)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true)) return true;
            }
            return false;
        }
        if (_terms.Length == 0) return false;
        foreach (string term in _terms)
        {
            if (name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) return false;
        }
        return true;
    }
}

/// <summary>
/// Finds files whose text contains every word of a query. Plain text is read as a stream, so a file of any size is
/// searched without loading it whole; documents (Word, PDF and so on) are read through <see cref="TextExtractor"/>.
/// </summary>
public sealed class ContentMatcher
{
    private const int ChunkChars = 64 * 1024;
    private const int SnippetRadius = 60;

    /// <summary>Plain text files larger than this are not searched.</summary>
    public const long MaxPlainBytes = 1024L * 1024 * 1024;

    private readonly string[] _terms;
    private readonly StringComparison _comparison;

    public ContentMatcher(string query, bool caseSensitive = false)
    {
        _terms = [.. QueryTerms.Split(query)];
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public bool IsEmpty => _terms.Length == 0;

    /// <summary>True when the file contains every word. <paramref name="snippet"/> is the text around the first word found.</summary>
    public bool Matches(string path, out string? snippet, CancellationToken token = default)
    {
        snippet = null;
        if (_terms.Length == 0) return false;
        TextFormat format = TextExtractor.FormatOf(Path.GetExtension(path));
        if (format is TextFormat.Plain or TextFormat.None) return MatchesPlain(path, format == TextFormat.None, out snippet, token);
        string? text = TextExtractor.Extract(path, 8_000_000, token: token);
        return text is not null && MatchesText(text, out snippet);
    }

    /// <summary>True when the text contains every word.</summary>
    public bool MatchesText(string text, out string? snippet)
    {
        snippet = null;
        foreach (string term in _terms)
        {
            int index = text.IndexOf(term, _comparison);
            if (index < 0)
            {
                snippet = null;
                return false;
            }
            snippet ??= Snippet(text, index, term.Length);
        }
        return true;
    }

    private bool MatchesPlain(string path, bool unknownType, out string? snippet, CancellationToken token)
    {
        snippet = null;
        try
        {
            using FileStream stream = TextSniffer.OpenShared(path);
            if (stream.Length > MaxPlainBytes) return false;
            // A file of unknown type is searched only when it looks like text.
            if (unknownType && stream.Length > 64L * 1024 * 1024) return false;
            byte[] sample = new byte[TextSniffer.SampleSize];
            int read = stream.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            Encoding? encoding = TextSniffer.Detect(sample.AsSpan(0, read));
            if (encoding is null) return false;
            stream.Position = 0;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            int overlap = _terms.Max(t => t.Length) - 1;
            char[] buffer = new char[ChunkChars + overlap];
            int carried = 0;
            var found = new bool[_terms.Length];
            int remaining = _terms.Length;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read2 = reader.ReadBlock(buffer, carried, ChunkChars);
                if (read2 == 0) break;
                int length = carried + read2;
                var window = new ReadOnlySpan<char>(buffer, 0, length);
                for (int t = 0; t < _terms.Length; t++)
                {
                    if (found[t]) continue;
                    int index = window.IndexOf(_terms[t], _comparison);
                    if (index < 0) continue;
                    found[t] = true;
                    remaining--;
                    snippet ??= Snippet(new string(window), index, _terms[t].Length);
                }
                if (remaining == 0) return true;
                // Keep the end of this block, so a word split across two blocks is still found.
                carried = Math.Min(overlap, length);
                Array.Copy(buffer, length - carried, buffer, 0, carried);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The line around a match, shortened to a readable length, with line breaks turned into spaces.</summary>
    public static string Snippet(string text, int index, int length)
    {
        int start = Math.Max(0, index - SnippetRadius);
        int end = Math.Min(text.Length, index + length + SnippetRadius);
        // The snippet stays on the line of the match.
        int lineStart = index == 0 ? -1 : text.LastIndexOf('\n', index - 1, index - start);
        if (lineStart >= 0) start = lineStart + 1;
        int afterMatch = Math.Min(text.Length, index + length);
        int lineEnd = text.IndexOf('\n', afterMatch, end - afterMatch);
        if (lineEnd >= 0) end = lineEnd;
        string piece = text[start..end].Replace('\r', ' ').Replace('\t', ' ').Trim();
        bool cutBefore = start > 0 && text[start - 1] != '\n';
        bool cutAfter = end < text.Length && text[end] != '\n';
        return (cutBefore ? "…" : "") + piece + (cutAfter ? "…" : "");
    }
}

/// <summary>A live search of one folder tree, with no index.</summary>
public sealed record SearchRequest(string Root, string Query, bool InNames, bool InContents, bool Recursive, bool IncludeHidden);

public sealed record SearchHit(FileEntry Entry, string? Snippet, bool NameMatched, bool ContentMatched);

/// <summary>Counters a running search updates for the status bar. Read them from any thread.</summary>
public sealed class SearchCounters
{
    private long _folders;
    private long _files;
    private long _hits;

    public long Folders => Interlocked.Read(ref _folders);

    public long Files => Interlocked.Read(ref _files);

    public long Hits => Interlocked.Read(ref _hits);

    internal void AddFolder() => Interlocked.Increment(ref _folders);

    internal void AddFile() => Interlocked.Increment(ref _files);

    internal void AddHit() => Interlocked.Increment(ref _hits);
}

/// <summary>
/// Walks a folder tree and reports matches as they are found. Names are checked while walking; file contents are
/// checked by several readers in parallel. Junctions and symbolic links to folders are not followed, so a loop such as
/// "Application Data" inside a profile is never walked twice.
/// </summary>
public static class LiveSearch
{
    /// <summary>The search stops after this many matches.</summary>
    public const int MaxHits = 50_000;

    public static async Task RunAsync(SearchRequest request, Action<SearchHit> onHit, SearchCounters counters, CancellationToken token)
    {
        var names = new NameMatcher(request.Query);
        var contents = new ContentMatcher(request.Query);
        bool searchNames = request.InNames && !names.IsEmpty;
        bool searchContents = request.InContents && !contents.IsEmpty;
        if (!searchNames && !searchContents) return;

        var queue = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(2048) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var listing = new ListingOptions(request.IncludeHidden);

        void Report(SearchHit hit)
        {
            counters.AddHit();
            onHit(hit);
            if (counters.Hits >= MaxHits) stop.Cancel();
        }

        Task walker = Task.Run(async () =>
        {
            try
            {
                var options = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = request.Recursive,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0,
                };
                var entries = new FileSystemEnumerable<FileEntry>(request.Root, DirectoryReader.ToEntry, options)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry entry) => DirectoryReader.IsVisible(entry.Attributes, listing),
                    ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                        (entry.Attributes & FileAttributes.ReparsePoint) == 0 && DirectoryReader.IsVisible(entry.Attributes, listing),
                };
                foreach (FileEntry entry in entries)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (entry.IsContainer) counters.AddFolder();
                    else counters.AddFile();
                    if (searchNames && names.Matches(entry.Name))
                    {
                        Report(new SearchHit(entry, null, NameMatched: true, ContentMatched: false));
                        continue;
                    }
                    // A cloud file that lives only online would be downloaded just to be searched, so only its name counts.
                    if (searchContents && !entry.IsContainer && !entry.IsOnlineOnly) await queue.Writer.WriteAsync(entry, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The root itself vanished or became unreadable; the matches so far stay.
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, CancellationToken.None);

        int readers = searchContents ? Math.Clamp(Environment.ProcessorCount / 2, 2, 8) : 0;
        var tasks = new List<Task> { walker };
        for (int i = 0; i < readers; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await foreach (FileEntry entry in queue.Reader.ReadAllAsync(stop.Token))
                    {
                        if (contents.Matches(entry.FullPath, out string? snippet, stop.Token))
                            Report(new SearchHit(entry, snippet, NameMatched: false, ContentMatched: true));
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}
