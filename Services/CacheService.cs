using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// A tiny on-disk index of parsed conversations, kept in %LOCALAPPDATA%\ClaudeCodeExplorer.
/// Entries are reused when a file's last-write time and size are unchanged, so repeat launches
/// avoid re-parsing every transcript.
/// </summary>
public sealed class CacheService
{
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, ConversationInfo> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public CacheService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeExplorer");
        // Bump the file name whenever the cached ConversationInfo shape changes, so an old
        // cache (e.g. one predating CustomTitle) is discarded and every transcript re-parsed
        // once, instead of serving stale entries for files whose size/mtime never change.
        _cachePath = Path.Combine(dir, "cache.v2.json");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json = File.ReadAllText(_cachePath);
            var list = JsonSerializer.Deserialize<List<ConversationInfo>>(json);
            if (list is null) return;
            foreach (var item in list)
                if (!string.IsNullOrEmpty(item.FilePath))
                    _entries[item.FilePath] = item;
        }
        catch
        {
            // A corrupt cache is never fatal – we just re-parse.
        }
    }

    public ConversationInfo? TryGet(FileInfo file)
    {
        if (_entries.TryGetValue(file.FullName, out var info)
            && info.FileWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks
            && info.FileSize == file.Length)
        {
            return info;
        }
        return null;
    }

    public void Set(ConversationInfo info) => _entries[info.FilePath] = info;

    /// <summary>Drop a single entry (used when a conversation is deleted from disk).</summary>
    public void Remove(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        _entries.TryRemove(filePath, out _);
    }

    /// <summary>Drop entries whose files have disappeared, then persist the index.</summary>
    public void Save(IReadOnlySet<string> livePaths)
    {
        try
        {
            foreach (var key in new List<string>(_entries.Keys))
                if (!livePaths.Contains(key))
                    _entries.TryRemove(key, out _);

            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_entries.Values);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Persisting the cache is best-effort.
        }
    }
}
