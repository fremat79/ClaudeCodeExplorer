using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using ClaudeCodeExplorer.Models;

namespace ClaudeCodeExplorer.Services;

/// <summary>
/// Parses a single Claude Code session (.jsonl) file into a <see cref="ConversationInfo"/>.
/// The file is read in one streaming pass; malformed lines are skipped rather than fatal.
/// </summary>
public static class JsonlParser
{
    private const int MaxStoredMessageChars = 1000;
    private const int MaxTitleChars = 200;

    /// <summary>Upper bound on characters indexed per conversation, to keep the search DB sane.</summary>
    private const int MaxIndexedTextChars = 200_000;

    public static ConversationInfo? Parse(FileInfo file)
    {
        var info = new ConversationInfo
        {
            SessionId = Path.GetFileNameWithoutExtension(file.Name),
            FilePath = file.FullName,
            ProjectFolderName = file.Directory?.Name ?? "",
            FileWriteTimeUtcTicks = file.LastWriteTimeUtc.Ticks,
            FileSize = file.Length,
            LastActivityUtc = file.LastWriteTimeUtc,
        };

        var summariesByLeaf = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastSummary = null;
        string? customTitle = null;
        string? lastMessageUuid = null;
        DateTime? minTs = null;
        DateTime? maxTs = null;
        bool haveFirstUser = false;
        bool haveCwd = false;
        bool haveBranch = false;
        int messageCount = 0;

        try
        {
            foreach (var raw in File.ReadLines(file.FullName))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw); }
                catch { continue; } // skip a malformed / partially-written line

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    string? type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;

                    if (type == "summary")
                    {
                        var summary = root.TryGetProperty("summary", out var s)
                            && s.ValueKind == JsonValueKind.String
                                ? s.GetString()
                                : null;
                        if (!string.IsNullOrWhiteSpace(summary))
                        {
                            lastSummary = summary;
                            if (root.TryGetProperty("leafUuid", out var leaf)
                                && leaf.ValueKind == JsonValueKind.String)
                            {
                                var leafId = leaf.GetString();
                                if (!string.IsNullOrEmpty(leafId))
                                    summariesByLeaf[leafId!] = summary!;
                            }
                        }
                        continue;
                    }

                    if (type == "custom-title")
                    {
                        // Written by Claude Code's /rename. Renaming again appends a new line,
                        // so the last non-empty value wins.
                        if (root.TryGetProperty("customTitle", out var ct)
                            && ct.ValueKind == JsonValueKind.String)
                        {
                            var value = ct.GetString();
                            if (!string.IsNullOrWhiteSpace(value)) customTitle = value;
                        }
                        continue;
                    }

                    if (type != "user" && type != "assistant") continue;

                    messageCount++;

                    if (!haveCwd && root.TryGetProperty("cwd", out var cwd)
                        && cwd.ValueKind == JsonValueKind.String)
                    {
                        info.WorkingDirectory = cwd.GetString() ?? "";
                        haveCwd = true;
                    }

                    if (!haveBranch && root.TryGetProperty("gitBranch", out var gb)
                        && gb.ValueKind == JsonValueKind.String)
                    {
                        var b = gb.GetString();
                        if (!string.IsNullOrWhiteSpace(b)) { info.GitBranch = b; haveBranch = true; }
                    }

                    if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(ts.GetString(), out var dto))
                    {
                        var u = dto.UtcDateTime;
                        if (minTs is null || u < minTs) minTs = u;
                        if (maxTs is null || u > maxTs) maxTs = u;
                    }

                    if (root.TryGetProperty("uuid", out var uu) && uu.ValueKind == JsonValueKind.String)
                        lastMessageUuid = uu.GetString();

                    if (type == "user" && !haveFirstUser && !IsMeta(root))
                    {
                        var text = ExtractText(root).Trim();
                        if (text.Length > 0 && !LooksLikeCommandOrSystem(text))
                        {
                            info.FirstUserMessage = Truncate(text, MaxStoredMessageChars);
                            haveFirstUser = true;
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // File in use or truncated mid-read: keep whatever we gathered so far.
        }

        info.MessageCount = messageCount;
        info.CreatedUtc = minTs;
        if (maxTs is { } mx && mx > info.LastActivityUtc) info.LastActivityUtc = mx;

        // Title: prefer a summary tied to the final leaf, then the most recent summary,
        // then the first user prompt, then a placeholder.
        string? title = null;
        if (lastMessageUuid is not null && summariesByLeaf.TryGetValue(lastMessageUuid, out var matched))
            title = matched;
        title ??= lastSummary;
        if (string.IsNullOrWhiteSpace(title))
            title = string.IsNullOrWhiteSpace(info.FirstUserMessage) ? "(no messages)" : info.FirstUserMessage;
        info.Title = Truncate(title!.Trim(), MaxTitleChars);

        info.CustomTitle = customTitle;
        info.ProjectName = DeriveProjectName(info.WorkingDirectory, info.ProjectFolderName);
        return info;
    }

    /// <summary>
    /// Reads the full conversation body (all real user + assistant text) for full-text indexing.
    /// Reuses <see cref="ExtractText"/> (which already ignores tool-use / thinking blocks); skips
    /// isMeta lines and command/system boilerplate, and caps total length. Never throws.
    /// </summary>
    public static string ExtractFullText(FileInfo file)
    {
        var sb = new StringBuilder();
        try
        {
            foreach (var raw in File.ReadLines(file.FullName))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString() : null;
                    if (type != "user" && type != "assistant") continue;
                    if (IsMeta(root)) continue;

                    var text = ExtractText(root).Trim();
                    if (text.Length == 0) continue;
                    if (type == "user" && LooksLikeCommandOrSystem(text)) continue;

                    sb.Append(text).Append('\n');
                    if (sb.Length >= MaxIndexedTextChars) break;
                }
            }
        }
        catch (IOException)
        {
            // File in use / truncated mid-read: index whatever we gathered so far.
        }

        return sb.ToString();
    }

    private static bool IsMeta(JsonElement root)
        => root.TryGetProperty("isMeta", out var m)
           && m.ValueKind == JsonValueKind.True;

    private static bool LooksLikeCommandOrSystem(string text)
        => text.StartsWith("<command-", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("Caveat:", StringComparison.OrdinalIgnoreCase)
           || text.StartsWith("<local-command", StringComparison.OrdinalIgnoreCase);

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return "";
        if (!msg.TryGetProperty("content", out var content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var el in content.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    sb.Append(el.GetString()).Append(' ');
                }
                else if (el.ValueKind == JsonValueKind.Object
                         && el.TryGetProperty("type", out var et) && et.GetString() == "text"
                         && el.TryGetProperty("text", out var txt)
                         && txt.ValueKind == JsonValueKind.String)
                {
                    sb.Append(txt.GetString()).Append(' ');
                }
            }
            return sb.ToString();
        }
        return "";
    }

    private static string DeriveProjectName(string cwd, string folderName)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var trimmed = cwd.TrimEnd('/', '\\');
            var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            var name = idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return folderName;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
