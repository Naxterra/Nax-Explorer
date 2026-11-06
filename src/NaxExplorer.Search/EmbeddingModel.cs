using System.Security.Cryptography;
using NaxExplorer.Core;

namespace NaxExplorer.Search;

/// <summary>One file of the model, pinned to a fixed revision and checked by its SHA-256 hash.</summary>
public sealed record ModelFile(string Name, string Url, long Size, string Sha256);

/// <summary>
/// The embedding model: Microsoft's multilingual-e5-small (MIT license), which understands German, English and about
/// 100 other languages and turns a piece of text into 384 numbers that describe its meaning. The files come from a
/// fixed revision on Hugging Face and are only kept when their hashes match.
/// </summary>
public static class EmbeddingModel
{
    public const string Name = "multilingual-e5-small";

    public const string Revision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";

    /// <summary>Stored in the index; a different value means every vector has to be computed again.</summary>
    public const string Id = Name + "@" + Revision;

    public const int Dimensions = 384;

    public const int MaxTokens = 512;

    private const string Base = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/" + Revision + "/onnx/";

    public static IReadOnlyList<ModelFile> Files { get; } =
    [
        new("sentencepiece.bpe.model", Base + "sentencepiece.bpe.model", 5_069_051, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
        new("model.onnx", Base + "model.onnx", 470_268_510, "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665"),
    ];

    public static long TotalBytes => Files.Sum(f => f.Size);

    public static string Folder => Path.Combine(DataFolders.Models, Name);

    public static string ModelPath => Path.Combine(Folder, "model.onnx");

    public static string TokenizerPath => Path.Combine(Folder, "sentencepiece.bpe.model");

    /// <summary>True when every file is present with its expected size. The hashes were checked when the files were saved.</summary>
    public static bool IsInstalled()
    {
        foreach (ModelFile file in Files)
        {
            var info = new FileInfo(Path.Combine(Folder, file.Name));
            if (!info.Exists || info.Length != file.Size) return false;
        }
        return true;
    }

    /// <summary>
    /// Downloads the missing files. Each file is written to a ".part" file, checked against its hash while it arrives,
    /// and renamed only when the hash matches, so a half-downloaded or altered model is never used.
    /// </summary>
    public static async Task DownloadAsync(IProgress<(long Done, long Total)>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Nax-Explorer/0.1");
        long done = 0;
        foreach (ModelFile file in Files)
        {
            string target = Path.Combine(Folder, file.Name);
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == file.Size)
            {
                done += file.Size;
                progress?.Report((done, TotalBytes));
                continue;
            }
            string part = target + ".part";
            using (HttpResponseMessage response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, TotalBytes));
                }
                string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (output.Length != file.Size || actual != file.Sha256)
                {
                    output.Close();
                    File.Delete(part);
                    throw new InvalidDataException($"{file.Name} does not match its published hash; it was not kept.");
                }
            }
            File.Move(part, target, overwrite: true);
        }
    }

    /// <summary>Removes the model files, for example when the user turns AI search off for good.</summary>
    public static void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }
}
