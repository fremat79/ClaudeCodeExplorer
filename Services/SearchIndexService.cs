using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Local full-text search index over the complete body of every conversation, backed by a
/// SQLite FTS5 database in %LOCALAPPDATA%\ClaudeCodeExplorer\search.db. Everything is in-process
/// (no server). The index is built incrementally in the background (see <see cref="BuildAsync"/>)
/// and queried on demand (see <see cref="Search"/>). It complements — never replaces — the fast
/// in-memory metadata filter, so search works before the index is complete.
/// </summary>
public sealed class SearchIndexService
{
    private readonly string _dbPath;

    public SearchIndexService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExplorer");
        _dbPath = Path.Combine(dir, "search.db");
    }

    private SqliteConnection OpenConnection()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            // WAL lets a search read while the background build writes; busy_timeout makes the
            // build and a concurrent Remove wait for each other instead of failing.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS docs (
    session_id  TEXT PRIMARY KEY,
    file_path   TEXT,
    write_ticks INTEGER,
    size        INTEGER
);
CREATE VIRTUAL TABLE IF NOT EXISTS doc_fts USING fts5 (
    body,
    session_id UNINDEXED,
    tokenize = 'unicode61 remove_diacritics 2'
);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Incrementally (re)indexes the given conversations. Compares each file's signature
    /// (write-time ticks + size) against the stored one and only re-reads changed / new files;
    /// prunes index rows whose conversation is no longer present. Runs entirely off the UI thread
    /// and reports (done, total) via <paramref name="progress"/>. Best-effort: never throws
    /// (except on cancellation).
    /// </summary>
    public Task BuildAsync(
        IReadOnlyList<ConversationInfo> conversations,
        IProgress<(int done, int total)>? progress,
        CancellationToken token)
    {
        return Task.Run(() =>
        {
            try
            {
                using var conn = OpenConnection();
                EnsureSchema(conn);

                var existing = new Dictionary<string, (long ticks, long size)>(StringComparer.Ordinal);
                using (var q = conn.CreateCommand())
                {
                    q.CommandText = "SELECT session_id, write_ticks, size FROM docs";
                    using var r = q.ExecuteReader();
                    while (r.Read())
                        existing[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
                }

                // Prune anything indexed that is no longer among the current conversations.
                var live = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in conversations) live.Add(c.SessionId);
                var toPrune = new List<string>();
                foreach (var id in existing.Keys)
                    if (!live.Contains(id)) toPrune.Add(id);
                if (toPrune.Count > 0)
                {
                    using var tx = conn.BeginTransaction();
                    foreach (var id in toPrune) DeleteById(conn, tx, id);
                    tx.Commit();
                    foreach (var id in toPrune) existing.Remove(id);
                }

                int total = conversations.Count;

                // Only re-read files whose signature changed (new/modified); the rest are already indexed.
                var needed = conversations.Where(c =>
                    !existing.TryGetValue(c.SessionId, out var sig)
                    || sig.ticks != c.FileWriteTimeUtcTicks
                    || sig.size != c.FileSize).ToList();

                int done = total - needed.Count; // unchanged conversations count as already done
                progress?.Report((done, total));

                var po = new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                };

                // Process in chunks: extract each chunk's text in PARALLEL (the heavy read + JSON
                // parse), then write it in one transaction. Keeps memory bounded, commits
                // incrementally (progress + cancellation-safe), and uses all cores for the slow part.
                const int chunkSize = 48;
                for (int i = 0; i < needed.Count; i += chunkSize)
                {
                    token.ThrowIfCancellationRequested();
                    int len = Math.Min(chunkSize, needed.Count - i);
                    var bodies = new string[len];
                    Parallel.For(0, len, po, j =>
                        bodies[j] = JsonlParser.ExtractFullText(new FileInfo(needed[i + j].FilePath)));

                    using (var tx = conn.BeginTransaction())
                    {
                        for (int j = 0; j < len; j++)
                        {
                            var c = needed[i + j];
                            DeleteById(conn, tx, c.SessionId);
                            using (var ins = conn.CreateCommand())
                            {
                                ins.Transaction = tx;
                                ins.CommandText = "INSERT INTO doc_fts(body, session_id) VALUES(@b, @id)";
                                ins.Parameters.AddWithValue("@b", bodies[j]);
                                ins.Parameters.AddWithValue("@id", c.SessionId);
                                ins.ExecuteNonQuery();
                            }
                            using (var up = conn.CreateCommand())
                            {
                                up.Transaction = tx;
                                up.CommandText =
                                    "INSERT OR REPLACE INTO docs(session_id, file_path, write_ticks, size) " +
                                    "VALUES(@id, @p, @t, @s)";
                                up.Parameters.AddWithValue("@id", c.SessionId);
                                up.Parameters.AddWithValue("@p", c.FilePath);
                                up.Parameters.AddWithValue("@t", c.FileWriteTimeUtcTicks);
                                up.Parameters.AddWithValue("@s", c.FileSize);
                                up.ExecuteNonQuery();
                            }
                        }
                        tx.Commit();
                    }

                    done += len;
                    progress?.Report((done, total));
                }

                progress?.Report((total, total));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Indexing is best-effort; a failure just means full-text results stay partial.
            }
        }, token);
    }

    /// <summary>Returns the session ids whose body matches every term in the query (AND, prefix).</summary>
    public HashSet<string> Search(string query)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var match = BuildMatch(query);
        if (match.Length == 0) return ids;

        try
        {
            // No EnsureSchema here: the schema is created by BuildAsync. If the index doesn't
            // exist yet the query throws and is caught below (no full-text hits until it's built).
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT session_id FROM doc_fts WHERE doc_fts MATCH @q";
            cmd.Parameters.AddWithValue("@q", match);
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
        }
        catch
        {
            // A malformed query or a locked/absent DB just yields no full-text hits.
        }
        return ids;
    }

    /// <summary>
    /// Closes and deletes the whole index database (and its WAL sidecars) so it can be rebuilt
    /// from scratch. Releases pooled connections first to free the file lock. Best-effort.
    /// Callers must ensure no build is running concurrently.
    /// </summary>
    public void DeleteDatabase()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort: a locked sidecar will be reused/overwritten on rebuild */ }
        }
    }

    /// <summary>Removes a conversation from the index. Best-effort; never throws.</summary>
    public void Remove(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        try
        {
            using var conn = OpenConnection();
            EnsureSchema(conn);
            using var tx = conn.BeginTransaction();
            DeleteById(conn, tx, sessionId);
            tx.Commit();
        }
        catch
        {
            // Best-effort: the prune pass in BuildAsync is the safety net.
        }
    }

    private static void DeleteById(SqliteConnection conn, SqliteTransaction? tx, string id)
    {
        using (var d1 = conn.CreateCommand())
        {
            d1.Transaction = tx;
            d1.CommandText = "DELETE FROM doc_fts WHERE session_id = @id";
            d1.Parameters.AddWithValue("@id", id);
            d1.ExecuteNonQuery();
        }
        using (var d2 = conn.CreateCommand())
        {
            d2.Transaction = tx;
            d2.CommandText = "DELETE FROM docs WHERE session_id = @id";
            d2.Parameters.AddWithValue("@id", id);
            d2.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Turns a free-text query into an FTS5 MATCH expression: each whitespace-separated term
    /// becomes a quoted prefix token ("term"*), joined by spaces (implicit AND). Quotes in the
    /// input are neutralised so the expression can never be malformed.
    /// </summary>
    private static string BuildMatch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var raw in terms)
        {
            var t = raw.Replace('"', ' ').Trim();
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(t).Append("\"*");
        }
        return sb.ToString();
    }
}
