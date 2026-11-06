using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NaxExplorer.Core;

namespace NaxExplorer.Tests;

internal static class LiveSearchTests
{
    public static IEnumerable<(string, string, Action)> All()
    {
        yield return ("name_search_matches_every_word_in_any_order", "\"upgrade oracle\" finds Oracle_Upgrade_19c.pdf but not oracle_patch.pdf.", () =>
        {
            var matcher = new NameMatcher("upgrade oracle");
            Check.That(matcher.Matches("Oracle_Upgrade_19c.pdf"), "should match");
            Check.That(!matcher.Matches("oracle_patch.pdf"), "should not match");
        });

        yield return ("wildcard_search_matches_any_of_several_patterns", "\"*.jpg;*.png\" matches both extensions and nothing else.", () =>
        {
            var matcher = new NameMatcher("*.jpg;*.png");
            Check.That(matcher.Matches("a.JPG") && matcher.Matches("b.png") && !matcher.Matches("c.gif"), "wildcards wrong");
        });

        yield return ("content_search_finds_a_word_split_across_read_blocks", "A word that starts at the end of one 64 KB read block and ends in the next is still found.", () =>
        {
            string folder = Fixtures.TempFolder("content");
            var text = new StringBuilder();
            text.Append('x', 64 * 1024 - 3);
            text.Append("needle");
            text.Append('y', 1000);
            string path = Fixtures.Write(folder, "big.log", text.ToString());
            Check.That(new ContentMatcher("NEEDLE").Matches(path, out _), "word across blocks not found");
        });

        yield return ("content_search_needs_every_word_and_reads_utf16", "A UTF-16 file matches only when it contains all words, and the snippet shows the line of the first word.", () =>
        {
            string folder = Fixtures.TempFolder("content");
            string path = Fixtures.Write(folder, "notes.txt", "first line\nUpgrade the database tonight\nlast line", Encoding.Unicode);
            Check.That(new ContentMatcher("database upgrade").Matches(path, out string? snippet), "should match");
            Check.Equal("Upgrade the database tonight", snippet, "snippet");
            Check.That(!new ContentMatcher("database rollback").Matches(path, out _), "should not match without every word");
        });

        yield return ("content_search_skips_binary_files", "A file with binary data is never reported as a text match.", () =>
        {
            string folder = Fixtures.TempFolder("content");
            string path = Path.Combine(folder, "tool.exe");
            byte[] bytes = new byte[4096];
            Encoding.ASCII.GetBytes("needle").CopyTo(bytes, 100);
            File.WriteAllBytes(path, bytes);
            Check.That(!new ContentMatcher("needle").Matches(path, out _), "binary file matched");
        });

        yield return ("content_search_never_reads_files_that_live_only_in_the_cloud", "A cloud file that is not on this device is never opened to search its contents, so the sync app does not download it, while its name still counts.", () =>
        {
            string folder = Fixtures.TempFolder("cloud");
            string local = Fixtures.Write(folder, "local.txt", "the budget is final");
            string online = Fixtures.Write(folder, "online.txt", "the budget is final");
            string named = Fixtures.Write(folder, "budget.txt", "nothing here");
            // The offline attribute is what Windows reports for content kept elsewhere; it can be set without a sync app.
            File.SetAttributes(online, FileAttributes.Offline);
            File.SetAttributes(named, FileAttributes.Offline);
            try
            {
                Check.That(CloudFiles.IsOnlineOnly(File.GetAttributes(online)), "the offline file was not seen as online-only");
                Check.That(CloudFiles.IsOnlineOnly(CloudFiles.RecallOnDataAccess) && !CloudFiles.IsOnlineOnly(FileAttributes.Directory | CloudFiles.RecallOnOpen), "cloud attributes misread");
                List<SearchHit> hits = Run(new SearchRequest(folder, "budget", InNames: true, InContents: true, Recursive: false, IncludeHidden: false));
                Check.Sequence(["budget.txt", "local.txt"], hits.Select(h => h.Entry.Name).Order(StringComparer.OrdinalIgnoreCase));
                Check.That(hits.Single(h => h.Entry.Name == "budget.txt").NameMatched, "the online-only file should match by its name");
            }
            finally
            {
                File.SetAttributes(online, FileAttributes.Normal);
                File.SetAttributes(named, FileAttributes.Normal);
            }
        });

        yield return ("content_readers_recheck_cloud_attributes_at_read_time", "Even when queued while local, a file that becomes online-only is refused by plain-text search and document extraction before it is opened.", () =>
        {
            string folder = Fixtures.TempFolder("cloud-read");
            string plain = Fixtures.Write(folder, "notes.txt", "budget needle");
            string document = Fixtures.Docx(folder, "notes.docx", "budget needle");
            foreach (string path in new[] { plain, document })
            {
                File.SetAttributes(path, FileAttributes.Offline);
                try
                {
                    Check.That(!new ContentMatcher("needle").Matches(path, out _), "content search read an online-only file");
                    Check.Equal<string?>(null, TextExtractor.Extract(path, 1000), "extraction read an online-only file");
                }
                finally
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
            }
        });

        yield return ("live_search_finds_names_and_contents_in_subfolders", "A recursive search reports name matches and content matches from subfolders, including inside a Word file.", () =>
        {
            string folder = Fixtures.TempFolder("live");
            Fixtures.Write(folder, @"a\oracle upgrade steps.txt", "nothing relevant");
            Fixtures.Write(folder, @"b\c\notes.md", "We plan the oracle upgrade on Friday.");
            Fixtures.Docx(Directory.CreateDirectory(Path.Combine(folder, "d")).FullName, "runbook.docx", "Oracle upgrade runbook");
            Fixtures.Write(folder, "unrelated.txt", "apples");
            var hits = Run(new SearchRequest(folder, "oracle upgrade", InNames: true, InContents: true, Recursive: true, IncludeHidden: false));
            Check.Sequence(["notes.md", "oracle upgrade steps.txt", "runbook.docx"], hits.Select(h => h.Entry.Name).Order());
            Check.That(hits.Single(h => h.Entry.Name == "oracle upgrade steps.txt").NameMatched, "name match flag");
        });

        yield return ("live_search_without_subfolders_stays_in_the_folder", "With subfolders turned off only the folder itself is searched.", () =>
        {
            string folder = Fixtures.TempFolder("live");
            Fixtures.Write(folder, "top match.txt", "x");
            Fixtures.Write(folder, @"sub\deep match.txt", "x");
            var hits = Run(new SearchRequest(folder, "match", InNames: true, InContents: false, Recursive: false, IncludeHidden: false));
            Check.Sequence(["top match.txt"], hits.Select(h => h.Entry.Name));
        });

        yield return ("live_search_does_not_follow_a_junction_back_into_its_parent", "A junction that points to its own parent folder is not walked, so the search ends and reports each file once.", () =>
        {
            string folder = Fixtures.TempFolder("junction");
            Fixtures.Write(folder, "match.txt", "x");
            string link = Path.Combine(folder, "loop");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            mklink.WaitForExit();
            if (!Directory.Exists(link)) throw new SkipException("the junction could not be created");
            var hits = Run(new SearchRequest(folder, "match", InNames: true, InContents: false, Recursive: true, IncludeHidden: false));
            Directory.Delete(link);
            Check.Equal(1, hits.Count, "hits");
        });
    }

    private static List<SearchHit> Run(SearchRequest request)
    {
        var hits = new ConcurrentBag<SearchHit>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        LiveSearch.RunAsync(request, hits.Add, new SearchCounters(), timeout.Token).GetAwaiter().GetResult();
        Check.That(!timeout.IsCancellationRequested, "search did not finish within 30 seconds");
        return [.. hits];
    }
}
