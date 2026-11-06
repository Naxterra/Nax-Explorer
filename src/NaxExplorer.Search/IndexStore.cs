using System.Text;
using Microsoft.Data.Sqlite;

namespace NaxExplorer.Search;

/// <summary>What the index knows about one file.</summary>
public enum FileState
{
    /// <summary>The text was read, split into pieces and given vectors.</summary>
    Indexed = 0,

    /// <summary>Only the name is known: the type holds no text the index reads, or the document had no text.</summary>
    NameOnly = 1,

    /// <summary>Reading the file failed; it is tried again when it changes.</summary>
    Failed = 2,
}

public sealed record KnownFile(long Id, long Size, long Modified, FileState State);

/// <summary>One piece of a document ready to be stored: its text and its vector.</summary>
public sealed record StoredChunk(string Text, sbyte[] Vector);

public sealed record FullTextHit(long ChunkId, long FileId, double Rank, string Snippet);

/// <summary>All vectors in one block of memory, for a fast scan at search time.</summary>
public sealed class VectorTable
{
    public required long[] ChunkIds { get; init; }

    public required long[] FileIds { get; init; }

    public required sbyte[] Data { get; init; }

    public required int Dimensions { get; init; }

    public int Count => ChunkIds.Length;

    public ReadOnlySpan<sbyte> Vector(int index) => Data.AsSpan(index * Dimensions, Dimensions);
}

/// <summary>
/// The AI index in one SQLite database: every file of the indexed folders, the text pieces of documents in a full-text
/// table (FTS5) for exact words, and one vector per piece for meaning. Every change to a file is one transaction, so a
/// crash leaves the file either fully old or fully new. The index only ever reads the user's files.
/// </summary>
public sealed class IndexStore : IDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>Marks the start and end of a matched word in full-text snippets.</summary>
    public const char MarkStart = '\u0002';
    public const char MarkEnd = '\u0003';

    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();
    private long _version;

    public string Path { get; }

    /// <summary>Goes up with every change, so searches know when cached data is out of date.</summary>
    public long Version => Interlocked.Read(ref _version);

    private IndexStore(string path, SqliteConnection connection)
    {
        Path = path;
        _connection = connection;
    }

    /// <summary>
    /// Opens or creates the index. A database that cannot be opened is moved aside as "index.damaged-*.db" and a new
    /// one is started, since the index can always be built again from the files themselves.
    /// </summary>
    public static IndexStore Open(string path, string modelId)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        try
        {
            return OpenOrThrow(path, modelId);
        }
        catch (SqliteException)
        {
            SqliteConnection.ClearAllPools();
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string aside = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, $"index.damaged-{stamp}.db");
            if (File.Exists(path)) File.Move(path, aside);
            foreach (string extra in (ReadOnlySpan<string>)[path + "-wal", path + "-shm"])
            {
                if (File.Exists(extra)) File.Delete(extra);
            }
            return OpenOrThrow(path, modelId);
        }
    }

    private static IndexStore OpenOrThrow(string path, string modelId)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            // SQLite's built-in NOCASE compares ASCII only. Scoped path comparisons need the same Unicode
            // case handling as the Windows paths used by navigation and semantic search. Keep this separate
            // from the schema's existing collation so opening an older index requires no index migration.
            connection.CreateCollation("NAX_PATH", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
            using (var check = connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check;";
                if (check.ExecuteScalar() as string != "ok") throw new SqliteException("The index failed its integrity check.", 11);
            }
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS files(
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    size INTEGER NOT NULL,
                    modified INTEGER NOT NULL,
                    state INTEGER NOT NULL,
                    indexed INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS chunks(
                    id INTEGER PRIMARY KEY,
                    file_id INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
                    ordinal INTEGER NOT NULL,
                    vector BLOB NOT NULL);
                CREATE INDEX IF NOT EXISTS chunks_by_file ON chunks(file_id);
                CREATE VIRTUAL TABLE IF NOT EXISTS chunk_text USING fts5(body, tokenize = 'unicode61 remove_diacritics 2');
                """);
            var store = new IndexStore(path, connection);
            string? schema = store.GetMeta("schema");
            if (schema is not null && schema != SchemaVersion.ToString()) throw new SqliteException("The index has an unknown layout.", 11);
            store.SetMeta("schema", SchemaVersion.ToString());
            if (store.GetMeta("model") != modelId)
            {
                // Vectors of another model cannot be compared with this one's, so everything is read again.
                store.Clear();
                store.SetMeta("model", modelId);
            }
            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string? GetMeta(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key, value) VALUES($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>Removes everything from the index.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Execute(_connection, "DELETE FROM chunk_text; DELETE FROM chunks; DELETE FROM files;");
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Every file the index knows, by path, to compare with what is on disk.</summary>
    public Dictionary<string, KnownFile> KnownFiles()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, KnownFile>(StringComparer.OrdinalIgnoreCase);
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, path, size, modified, state FROM files";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(1)] = new KnownFile(reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(3), (FileState)reader.GetInt32(4));
            }
            return result;
        }
    }

    public KnownFile? Find(string path)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, size, modified, state FROM files WHERE path = $path";
            command.Parameters.AddWithValue("$path", path);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? new KnownFile(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), (FileState)reader.GetInt32(3)) : null;
        }
    }

    /// <summary>Stores a file and its pieces, replacing whatever was stored for it before, in one transaction.</summary>
    public void Save(string path, long size, long modified, FileState state, IReadOnlyList<StoredChunk> chunks)
    {
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            long? oldId = FileId(path, transaction);
            if (oldId is { } id) DeleteFile(id, transaction);
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO files(path, size, modified, state, indexed) VALUES($path, $size, $modified, $state, $indexed) RETURNING id";
                insert.Parameters.AddWithValue("$path", path);
                insert.Parameters.AddWithValue("$size", size);
                insert.Parameters.AddWithValue("$modified", modified);
                insert.Parameters.AddWithValue("$state", (int)state);
                insert.Parameters.AddWithValue("$indexed", DateTime.UtcNow.Ticks);
                long fileId = (long)insert.ExecuteScalar()!;
                using var chunk = _connection.CreateCommand();
                chunk.Transaction = transaction;
                chunk.CommandText = "INSERT INTO chunks(file_id, ordinal, vector) VALUES($file, $ordinal, $vector) RETURNING id";
                var fileParameter = chunk.Parameters.Add("$file", SqliteType.Integer);
                var ordinalParameter = chunk.Parameters.Add("$ordinal", SqliteType.Integer);
                var vectorParameter = chunk.Parameters.Add("$vector", SqliteType.Blob);
                using var text = _connection.CreateCommand();
                text.Transaction = transaction;
                text.CommandText = "INSERT INTO chunk_text(rowid, body) VALUES($id, $body)";
                var idParameter = text.Parameters.Add("$id", SqliteType.Integer);
                var bodyParameter = text.Parameters.Add("$body", SqliteType.Text);
                for (int i = 0; i < chunks.Count; i++)
                {
                    fileParameter.Value = fileId;
                    ordinalParameter.Value = i;
                    vectorParameter.Value = System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunks[i].Vector.AsSpan()).ToArray();
                    long chunkId = (long)chunk.ExecuteScalar()!;
                    idParameter.Value = chunkId;
                    bodyParameter.Value = chunks[i].Text;
                    text.ExecuteNonQuery();
                }
            }
            transaction.Commit();
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Records many files by name only, in one transaction. Files already stored with text keep it.</summary>
    public void SaveNamesOnly(IReadOnlyList<(string Path, long Size, long Modified)> files)
    {
        if (files.Count == 0) return;
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO files(path, size, modified, state, indexed) VALUES($path, $size, $modified, 1, $indexed)
                ON CONFLICT(path) DO UPDATE SET size = excluded.size, modified = excluded.modified
                """;
            var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
            var sizeParameter = command.Parameters.Add("$size", SqliteType.Integer);
            var modifiedParameter = command.Parameters.Add("$modified", SqliteType.Integer);
            command.Parameters.AddWithValue("$indexed", DateTime.UtcNow.Ticks);
            foreach (var (path, size, modified) in files)
            {
                pathParameter.Value = path;
                sizeParameter.Value = size;
                modifiedParameter.Value = modified;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            Interlocked.Increment(ref _version);
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            if (FileId(path, transaction) is { } id) DeleteFile(id, transaction);
            transaction.Commit();
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Removes many files at once, for example everything a scan no longer finds.</summary>
    public void Remove(IEnumerable<long> fileIds)
    {
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            foreach (long id in fileIds) DeleteFile(id, transaction);
            transaction.Commit();
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Removes every file inside a folder, for example after the folder was deleted or renamed.</summary>
    public void RemoveUnder(string folder)
    {
        string prefix = folder.TrimEnd('\\') + "\\";
        lock (_gate)
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var ids = new List<long>();
            using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                // "]" follows "\" in character order, so this range is exactly the paths that start with the prefix.
                command.CommandText = "SELECT id FROM files WHERE path >= $low AND path < $high";
                command.Parameters.AddWithValue("$low", prefix);
                command.Parameters.AddWithValue("$high", prefix[..^1] + "]");
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetInt64(0));
            }
            foreach (long id in ids) DeleteFile(id, transaction);
            transaction.Commit();
            Interlocked.Increment(ref _version);
        }
    }

    private long? FileId(string path, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM files WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private void DeleteFile(long id, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM chunk_text WHERE rowid IN (SELECT id FROM chunks WHERE file_id = $id);
            DELETE FROM chunks WHERE file_id = $id;
            DELETE FROM files WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public (int Files, int Documents, int Chunks) Counts()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT (SELECT count(*) FROM files), (SELECT count(*) FROM files WHERE state = 0), (SELECT count(*) FROM chunks)";
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }
    }

    /// <summary>Every file's id and path, for matching names.</summary>
    public List<(long Id, string Path)> AllPaths()
    {
        lock (_gate)
        {
            var result = new List<(long, string)>();
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, path FROM files";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetString(1)));
            return result;
        }
    }

    public VectorTable LoadVectors()
    {
        lock (_gate)
        {
            int count;
            using (var counter = _connection.CreateCommand())
            {
                counter.CommandText = "SELECT count(*) FROM chunks";
                count = Convert.ToInt32(counter.ExecuteScalar());
            }
            int dimensions = EmbeddingModel.Dimensions;
            var chunkIds = new long[count];
            var fileIds = new long[count];
            var data = new sbyte[count * dimensions];
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, file_id, vector FROM chunks";
            using SqliteDataReader reader = command.ExecuteReader();
            int row = 0;
            while (reader.Read() && row < count)
            {
                byte[] blob = (byte[])reader.GetValue(2);
                if (blob.Length != dimensions) continue;
                chunkIds[row] = reader.GetInt64(0);
                fileIds[row] = reader.GetInt64(1);
                Buffer.BlockCopy(blob, 0, data, row * dimensions, dimensions);
                row++;
            }
            if (row < count)
            {
                Array.Resize(ref chunkIds, row);
                Array.Resize(ref fileIds, row);
                Array.Resize(ref data, row * dimensions);
            }
            return new VectorTable { ChunkIds = chunkIds, FileIds = fileIds, Data = data, Dimensions = dimensions };
        }
    }

    /// <summary>
    /// Pieces that contain the words, best first by BM25. Every word must appear unless <paramref name="anyWord"/> is
    /// set. Words match as prefixes, so "upgrad" finds "upgrade" and "upgrades".
    /// </summary>
    public List<FullTextHit> FullText(IReadOnlyList<string> terms, bool anyWord, int limit, string? scope = null)
    {
        string? match = BuildMatch(terms, anyWord);
        if (match is null) return [];
        lock (_gate)
        {
            var result = new List<FullTextHit>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT chunk_text.rowid, chunks.file_id, bm25(chunk_text), snippet(chunk_text, 0, $start, $end, '…', 16)
                FROM chunk_text JOIN chunks ON chunks.id = chunk_text.rowid JOIN files ON files.id = chunks.file_id
                WHERE chunk_text MATCH $match AND ($scope IS NULL OR files.path = $scope COLLATE NAX_PATH
                    OR substr(files.path, 1, length($prefix)) = $prefix COLLATE NAX_PATH)
                ORDER BY bm25(chunk_text)
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$start", MarkStart.ToString());
            command.Parameters.AddWithValue("$end", MarkEnd.ToString());
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$scope", (object?)scope ?? DBNull.Value);
            command.Parameters.AddWithValue("$prefix", scope is null ? DBNull.Value : scope.TrimEnd('\\', '/') + "\\");
            try
            {
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read()) result.Add(new FullTextHit(reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetString(3)));
            }
            catch (SqliteException)
            {
                // A query FTS5 cannot parse finds nothing instead of failing the search.
            }
            return result;
        }
    }

    /// <summary>
    /// Turns words into an FTS5 query. Each word is quoted, so characters such as - or : are never read as operators,
    /// and quotes inside a word are doubled as FTS5 requires.
    /// </summary>
    internal static string? BuildMatch(IReadOnlyList<string> terms, bool anyWord)
    {
        var parts = new List<string>();
        foreach (string term in terms)
        {
            string cleaned = term.Replace("\"", "\"\"").Trim();
            if (cleaned.Length == 0 || !cleaned.Any(char.IsLetterOrDigit)) continue;
            // A phrase of several words matches as a phrase; a single word also matches longer words that start with it.
            parts.Add(cleaned.Contains(' ') ? $"\"{cleaned}\"" : $"\"{cleaned}\"*");
        }
        if (parts.Count == 0) return null;
        return string.Join(anyWord ? " OR " : " AND ", parts);
    }

    public string? ChunkText(long chunkId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT body FROM chunk_text WHERE rowid = $id";
            command.Parameters.AddWithValue("$id", chunkId);
            return command.ExecuteScalar() as string;
        }
    }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string text, int length) => text.Length <= length ? text : text[..length] + "…";

    public static string Collapse(this string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
