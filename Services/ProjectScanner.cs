using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Enumerates ~/.claude/projects and parses every session file it finds, using the
/// <see cref="CacheService"/> to skip files that have not changed.
/// </summary>
public static class ProjectScanner
{
    public static string ProjectsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    public static List<ConversationInfo> Scan(CacheService cache, CancellationToken token = default)
    {
        if (!Directory.Exists(ProjectsRoot))
            return new List<ConversationInfo>();

        var files = new List<FileInfo>();
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            token.ThrowIfCancellationRequested();
            IEnumerable<string> sessionFiles;
            try { sessionFiles = Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly); }
            catch { continue; }

            foreach (var f in sessionFiles)
            {
                try { files.Add(new FileInfo(f)); }
                catch { /* ignore inaccessible file */ }
            }
        }

        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) livePaths.Add(f.FullName);

        var results = new ConcurrentBag<ConversationInfo>();
        var options = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
        };

        Parallel.ForEach(files, options, file =>
        {
            var cached = cache.TryGet(file);
            if (cached is not null) { results.Add(cached); return; }

            var parsed = JsonlParser.Parse(file);
            if (parsed is not null)
            {
                cache.Set(parsed);
                results.Add(parsed);
            }
        });

        cache.Save(livePaths);

        var list = new List<ConversationInfo>(results);
        list.Sort((a, b) => b.LastActivityUtc.CompareTo(a.LastActivityUtc));
        return list;
    }
}
