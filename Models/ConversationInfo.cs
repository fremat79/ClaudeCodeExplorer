using System;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ClaudeCodeExplorer.Models;

/// <summary>
/// Metadata describing a single Claude Code conversation (one .jsonl session file
/// under ~/.claude/projects). This type is also persisted to the on-disk cache, so
/// every stored property must stay serializable by System.Text.Json.
/// </summary>
public sealed class ConversationInfo : INotifyPropertyChanged
{
    /// <summary>The session id (the .jsonl file name without extension). Used by `claude --resume`.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Absolute path to the .jsonl file.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Encoded folder name under ~/.claude/projects (path separators flattened to '-').</summary>
    public string ProjectFolderName { get; set; } = "";

    /// <summary>The real working directory, read from the "cwd" field inside the transcript.</summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Friendly project name (last segment of the working directory).</summary>
    public string ProjectName { get; set; } = "";

    /// <summary>Best available title: Claude's own summary if present, otherwise the first user prompt.</summary>
    public string Title { get; set; } = "";

    /// <summary>Explicit session name set via Claude Code's /rename, if any.</summary>
    public string? CustomTitle { get; set; }

    /// <summary>The first real user message, used for the tooltip preview and search.</summary>
    public string FirstUserMessage { get; set; } = "";

    /// <summary>Number of user/assistant messages in the transcript.</summary>
    public int MessageCount { get; set; }

    /// <summary>Git branch recorded in the transcript, if any.</summary>
    public string? GitBranch { get; set; }

    /// <summary>Timestamp of the first message (UTC), if parseable.</summary>
    public DateTime? CreatedUtc { get; set; }

    /// <summary>Most recent activity (UTC) – the later of the file's last-write time and the last message timestamp.</summary>
    public DateTime LastActivityUtc { get; set; }

    // --- cache validation fields ---
    public long FileWriteTimeUtcTicks { get; set; }
    public long FileSize { get; set; }

    // --- transient UI state (not persisted) ---
    private bool _isSelected;

    /// <summary>Whether this tile is checked for batch deletion. Transient — never cached.</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The small accent heading on the tile: the explicit /rename title when one was set,
    /// otherwise the session GUID. Using the id (rather than the auto title) for un-named
    /// sessions keeps the heading distinct from the message shown in the body — no duplication —
    /// while still giving every tile an identifier.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle! : SessionId;

    /// <summary>
    /// Conversation extract shown in the tile body — the first real user message, falling back to
    /// the auto title so the body is never empty when the session has content.
    /// </summary>
    [JsonIgnore]
    public string PreviewText => !string.IsNullOrEmpty(FirstUserMessage) ? FirstUserMessage : Title;

    [JsonIgnore]
    private string? _searchBlob;

    /// <summary>Lower-cased, concatenated text used for fast in-memory search filtering.</summary>
    [JsonIgnore]
    public string SearchBlob =>
        _searchBlob ??= string.Join('\n',
            Title, CustomTitle ?? "", FirstUserMessage, ProjectName, WorkingDirectory, GitBranch ?? "", SessionId)
            .ToLowerInvariant();

    [JsonIgnore]
    private string? _normalizedSearchBlob;

    /// <summary>Accent- and case-insensitive form of <see cref="SearchBlob"/> for the in-memory filter.</summary>
    [JsonIgnore]
    public string NormalizedSearchBlob =>
        _normalizedSearchBlob ??= Services.TextNormalizer.Normalize(SearchBlob);
}
