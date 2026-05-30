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

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyIdCommand { get; }
    public RelayCommand DeleteCommand { get; }

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

            _conversations.Clear();
            foreach (var c in list) _conversations.Add(c);
            ConversationsView.Refresh();

            if (list.Count > 0)
            {
                StatusText = $"{list.Count} conversation{(list.Count == 1 ? "" : "s")}";
            }
            else
            {
                StatusText = Directory.Exists(ProjectScanner.ProjectsRoot)
                    ? "No conversations found."
                    : $"Folder not found: {ProjectScanner.ProjectsRoot}";
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

        if (File.Exists(c.FilePath))
            File.Delete(c.FilePath);

        // If that was the last session stored under ~/.claude/projects/<encoded>,
        // remove the now-empty project folder too. This is the storage folder, NOT
        // the real working directory the conversation ran in.
        TryRemoveEmptyProjectFolder(c.FilePath);

        _cache.Remove(c.FilePath);
        _conversations.Remove(c);
        ConversationsView.Refresh();

        var n = _conversations.Count;
        StatusText = n == 0 ? "No conversations found." : $"{n} conversation{(n == 1 ? "" : "s")}";
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
