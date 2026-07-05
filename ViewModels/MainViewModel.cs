using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using ClaudeCodeExplorer.Models;
using ClaudeCodeExplorer.Services;

namespace ClaudeCodeExplorer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CacheService _cache = new();
    private readonly ObservableCollection<ConversationInfo> _conversations = new();

    /// <summary>Filtered + sorted view bound by the UI.</summary>
    public ICollectionView ConversationsView { get; }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ConversationsView.Refresh(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _hasSelection;
    /// <summary>True when at least one conversation is checked — drives the "Delete selected" button's enabled state.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetProperty(ref _hasSelection, value);
    }

    private int _retentionDays = 180;
    /// <summary>Age threshold (in days) for the "delete older than" maintenance action. Never below 1.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, Math.Max(1, value)); // clamp: no negatives / no 0
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DeleteEmptyCommand { get; }
    public RelayCommand DeleteOlderCommand { get; }
    public RelayCommand IncrementDaysCommand { get; }
    public RelayCommand DecrementDaysCommand { get; }
    public RelayCommand SelectAllInGroupCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }

    public MainViewModel()
    {
        ConversationsView = CollectionViewSource.GetDefaultView(_conversations);
        ConversationsView.SortDescriptions.Add(
            new SortDescription(nameof(ConversationInfo.LastActivityUtc), ListSortDirection.Descending));
        // Group by the full working directory (unique), then we display the project's
        // last path segment in the header via PathLeafConverter. Filter is applied
        // before grouping, so groups with no matching items simply do not appear.
        ConversationsView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ConversationInfo.WorkingDirectory)));
        ConversationsView.Filter = FilterPredicate;

        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        ResumeCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.Resume(c); }));
        OpenTerminalCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.OpenTerminal(c); }));
        OpenFolderCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.OpenInExplorer(c); }));
        CopyIdCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) Clipboard.SetText(c.SessionId); }));
        DeleteCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) Delete(c); }));
        DeleteEmptyCommand = new RelayCommand(_ => Run(DeleteEmpty));
        DeleteOlderCommand = new RelayCommand(_ => Run(DeleteOlder));
        IncrementDaysCommand = new RelayCommand(_ => RetentionDays++);
        DecrementDaysCommand = new RelayCommand(_ => RetentionDays--);
        SelectAllInGroupCommand = new RelayCommand(SelectAllInGroup);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        DeleteSelectedCommand = new RelayCommand(_ => Run(DeleteSelected));

        _cache.Load();
        _ = LoadAsync();
    }

    private bool FilterPredicate(object obj)
    {
        if (string.IsNullOrWhiteSpace(_searchText)) return true;
        if (obj is not ConversationInfo c) return false;
        var q = _searchText.Trim().ToLowerInvariant();
        return c.SearchBlob.Contains(q, StringComparison.Ordinal);
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "Scanning ~/.claude/projects …";
        try
        {
            var list = await Task.Run(() => ProjectScanner.Scan(_cache));

            foreach (var c in _conversations) c.PropertyChanged -= OnConversationPropertyChanged;
            _conversations.Clear();
            foreach (var c in list)
            {
                c.PropertyChanged += OnConversationPropertyChanged;
                _conversations.Add(c);
            }
            ConversationsView.Refresh();

            if (list.Count > 0)
            {
                UpdateCountStatus();
            }
            else
            {
                StatusText = Directory.Exists(ProjectScanner.ProjectsRoot)
                    ? "No conversations found."
                    : $"Folder not found: {ProjectScanner.ProjectsRoot}";
                HasSelection = false;
            }
        }
        catch (Exception ex)
        {
            StatusText = "Error: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Delete(ConversationInfo c)
    {
        var answer = MessageBox.Show(
            "Permanently delete this conversation?\n\n"
            + c.Title + "\n\n"
            + "This removes the session file from disk and cannot be undone.",
            "Claude Code Explorer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        RemoveConversationCore(c);
        ConversationsView.Refresh();
        UpdateCountStatus();
    }

    /// <summary>Delete every conversation with no messages, after a single confirmation.</summary>
    private void DeleteEmpty()
    {
        var empties = _conversations.Where(c => c.MessageCount == 0).ToList();
        if (empties.Count == 0)
        {
            MessageBox.Show("No empty conversations to delete.", "Claude Code Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            $"Delete {empties.Count} empty conversation{(empties.Count == 1 ? "" : "s")}?\n\n"
            + "This removes the session files from disk and cannot be undone.",
            "Claude Code Explorer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        foreach (var c in empties) RemoveConversationCore(c);
        ConversationsView.Refresh();
        UpdateCountStatus();
    }

    /// <summary>
    /// Delete every conversation whose last activity is older than <see cref="RetentionDays"/>,
    /// after a confirmation that reports the count and the folders that will be affected.
    /// </summary>
    private void DeleteOlder()
    {
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        var old = _conversations.Where(c => c.LastActivityUtc < cutoff).ToList();
        if (old.Count == 0)
        {
            MessageBox.Show($"No conversations older than {RetentionDays} days.", "Claude Code Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Summarise the working directories the doomed conversations live in.
        var folders = old
            .GroupBy(c => string.IsNullOrWhiteSpace(c.WorkingDirectory) ? "(unknown folder)" : c.WorkingDirectory)
            .OrderByDescending(g => g.Count())
            .ToList();

        const int maxShown = 20;
        var lines = folders.Take(maxShown).Select(g => $"  • {g.Key}  ({g.Count()})");
        var more = folders.Count > maxShown ? $"\n  …and {folders.Count - maxShown} more folder(s)" : "";

        var answer = MessageBox.Show(
            $"Delete {old.Count} conversation{(old.Count == 1 ? "" : "s")} older than {RetentionDays} days?\n\n"
            + $"Affected folders ({folders.Count}):\n"
            + string.Join("\n", lines) + more
            + "\n\nThis removes the session files from disk and cannot be undone.",
            "Claude Code Explorer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        foreach (var c in old) RemoveConversationCore(c);
        ConversationsView.Refresh();
        UpdateCountStatus();
    }

    /// <summary>
    /// Toggles selection for every conversation in one folder group: selects all if any are
    /// unselected, otherwise clears the group. The parameter is the group's CollectionViewGroup.
    /// </summary>
    private void SelectAllInGroup(object? parameter)
    {
        if (parameter is not CollectionViewGroup group) return;
        var items = group.Items.OfType<ConversationInfo>().ToList();
        bool allSelected = items.Count > 0 && items.All(c => c.IsSelected);
        foreach (var c in items) c.IsSelected = !allSelected;
    }

    private void ClearSelection()
    {
        foreach (var c in _conversations) c.IsSelected = false;
    }

    /// <summary>
    /// Delete every checked conversation (across all folders, ignoring the search filter),
    /// after a confirmation reporting the count and the affected folders.
    /// </summary>
    private void DeleteSelected()
    {
        var selected = _conversations.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("No conversations selected.", "Claude Code Explorer",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folders = selected
            .GroupBy(c => string.IsNullOrWhiteSpace(c.WorkingDirectory) ? "(unknown folder)" : c.WorkingDirectory)
            .OrderByDescending(g => g.Count())
            .ToList();

        const int maxShown = 20;
        var lines = folders.Take(maxShown).Select(g => $"  • {g.Key}  ({g.Count()})");
        var more = folders.Count > maxShown ? $"\n  …and {folders.Count - maxShown} more folder(s)" : "";

        var answer = MessageBox.Show(
            $"Delete {selected.Count} selected conversation{(selected.Count == 1 ? "" : "s")}?\n\n"
            + $"Affected folders ({folders.Count}):\n"
            + string.Join("\n", lines) + more
            + "\n\nThis removes the session files from disk and cannot be undone.",
            "Claude Code Explorer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes) return;

        foreach (var c in selected) RemoveConversationCore(c);
        ConversationsView.Refresh();
        UpdateCountStatus();
    }

    /// <summary>
    /// Removes one conversation from disk, cache and the in-memory collection. No dialog and no
    /// status update, so it can back both the single-item delete and the bulk maintenance actions.
    /// </summary>
    private void RemoveConversationCore(ConversationInfo c)
    {
        if (File.Exists(c.FilePath))
            File.Delete(c.FilePath);

        // If that was the last session stored under ~/.claude/projects/<encoded>,
        // remove the now-empty project folder too. This is the storage folder, NOT
        // the real working directory the conversation ran in.
        TryRemoveEmptyProjectFolder(c.FilePath);

        _cache.Remove(c.FilePath);
        c.PropertyChanged -= OnConversationPropertyChanged;
        _conversations.Remove(c);
    }

    /// <summary>Keeps <see cref="HasSelection"/> in sync as tiles are checked/unchecked.</summary>
    private void OnConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationInfo.IsSelected))
            HasSelection = _conversations.Any(c => c.IsSelected);
    }

    private void UpdateCountStatus()
    {
        var n = _conversations.Count;
        StatusText = n == 0 ? "No conversations found." : $"{n} conversation{(n == 1 ? "" : "s")}";
        HasSelection = _conversations.Any(c => c.IsSelected);
    }

    /// <summary>
    /// Removes the ~/.claude/projects/&lt;encoded&gt; folder that stored a session, but only
    /// once it holds no more .jsonl transcripts. Guarded so it can only ever delete a
    /// folder that sits directly under <see cref="ProjectScanner.ProjectsRoot"/>.
    /// </summary>
    private static void TryRemoveEmptyProjectFolder(string sessionFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(sessionFilePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            var parent = Path.GetDirectoryName(dir);
            if (parent is null) return;

            var projectsRoot = Path.GetFullPath(ProjectScanner.ProjectsRoot).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetFullPath(parent).TrimEnd('\\', '/'), projectsRoot,
                    StringComparison.OrdinalIgnoreCase))
                return; // not a project-storage folder – leave it alone

            // Other conversations still live here? Keep the folder.
            if (Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.TopDirectoryOnly).Any())
                return;

            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup – never let it block the conversation deletion.
        }
    }

    private static void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Claude Code Explorer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
