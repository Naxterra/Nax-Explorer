using NaxExplorer.Tests;

// Console test runner: prints every test as file::name, its scenario and PASS, FAIL or SKIP. The exit code is the
// number of failures. A SKIP names what was not available, and that scenario counts as not verified.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
var suites = new (string File, IEnumerable<(string Name, string Scenario, Action Body)> Tests)[]
{
    ("CoreTests.cs", CoreTests.All()),
    ("ExtractionTests.cs", ExtractionTests.All()),
    ("LiveSearchTests.cs", LiveSearchTests.All()),
    ("IndexTests.cs", IndexTests.All()),
    ("SearchServiceTests.cs", SearchServiceTests.All()),
    ("RegressionTests.cs", RegressionTests.All()),
    ("TransitionTests.cs", TransitionTests.All()),
    ("ShellTests.cs", ShellTests.All()),
    ("ModelTests.cs", ModelTests.All()),
};

string? only = args.Length > 0 ? args[0] : null;
int failures = 0, skipped = 0, count = 0;
foreach (var (file, tests) in suites)
{
    foreach (var (name, scenario, body) in tests)
    {
        if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase) && !file.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
        count++;
        string result;
        try
        {
            body();
            result = "PASS";
        }
        catch (SkipException ex)
        {
            skipped++;
            result = "SKIP: " + ex.Message;
        }
        catch (Exception ex)
        {
            failures++;
            result = "FAIL: " + ex.Message;
        }
        Console.WriteLine($"{file}::{name} | {scenario} | {result}");
    }
}
Fixtures.Cleanup();
if (count == 0)
{
    Console.Error.WriteLine($"No tests matched '{only}'.");
    return 1;
}
Console.WriteLine($"{count - failures - skipped} passed, {failures} failed, {skipped} skipped ({count} tests).");
return failures;

namespace NaxExplorer.Tests
{
    internal sealed class SkipException(string message) : Exception(message);

    internal static class Check
    {
        public static void That(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string? what = null)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what ?? "value"}: expected {expected}, got {actual}");
        }

        public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? what = null)
        {
            var e = expected.ToList();
            var a = actual.ToList();
            if (!e.SequenceEqual(a)) throw new Exception($"{what ?? "sequence"}: expected [{string.Join(", ", e)}], got [{string.Join(", ", a)}]");
        }
    }

    /// <summary>Temporary folders, removed when the run ends.</summary>
    internal static class Fixtures
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "nax-explorer-tests-" + Environment.ProcessId);

        public static string TempFolder(string name)
        {
            string folder = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            return folder;
        }

        public static string Write(string folder, string relative, string content, System.Text.Encoding? encoding = null)
        {
            string path = Path.Combine(folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, encoding ?? new System.Text.UTF8Encoding(false));
            return path;
        }

        public static void Cleanup()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>A minimal Office Open XML file with the given parts.</summary>
        public static string Zip(string folder, string name, params (string Part, string Xml)[] parts)
        {
            string path = Path.Combine(folder, name);
            using var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var (part, xml) in parts)
            {
                var entry = zip.CreateEntry(part);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(xml);
            }
            return path;
        }

        public static string Docx(string folder, string name, params string[] paragraphs)
        {
            string body = string.Concat(paragraphs.Select(p => $"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>"));
            return Zip(folder, name, ("word/document.xml",
                $"<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}</w:body></w:document>"));
        }
    }
}
