using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ClaudeCodeExplorer.Models;
using ClaudeCodeExplorer.Services;

namespace ClaudeCodeExplorer.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly CacheService _cache = new();
    private readonly SearchIndexService _index = new();
    private readonly ObservableCollection<ConversationInfo> _conversations = new();

    /// <summary>Session ids matching the current query via the full-text index (added to the filter).</summary>
    private HashSet<string> _ftsIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _indexCts;
    private Task? _indexTask;
    private bool _isRebuilding;

    /// <summary>Normalised query terms (accent/case-folded), computed once per query — not per item.</summary>
    private string[] _queryTerms = Array.Empty<string>();
    /// <summary>Coalesces keystrokes so filtering runs once the user pauses, not on every key.</summary>
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>Max tiles rendered for a search. Caps the (non-virtualized) render cost per keystroke.</summary>
    private const int MaxSearchResults = 120;
    /// <summary>Remaining slots for the current filter pass; decremented as items pass the filter.</summary>
    private int _matchBudget;

    private bool _useFullTextSearch = SettingsService.GetFullTextEnabled();
    /// <summary>
    /// When true, search also consults the SQLite full-text index (whole conversation body) and the
    /// index is built/kept in the background. Off by default; the choice is persisted across runs.
    /// </summary>
    public bool UseFullTextSearch
    {
        get => _useFullTextSearch;
        set
        {
            if (!SetProperty(ref _useFullTextSearch, value)) return;
            SettingsService.SetFullTextEnabled(value);
            if (value)
            {
                StartIndexing();          // build/refresh the index in the background
                ApplySearch();            // re-run current query, now including full-text
            }
            else
            {
                _indexCts?.Cancel();      // stop any running build
                IsIndexing = false;
                _ftsIds = new HashSet<string>(StringComparer.Ordinal);
                RefreshResults();         // re-filter on metadata only
            }
        }
    }

    /// <summary>Filtered + sorted view bound by the UI.</summary>
    public ICollectionView ConversationsView { get; }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            // Debounce: (re)start the timer; the actual filtering runs once typing settles.
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    /// <summary>
    /// Applies the current query: normalises the terms once, refreshes the view (metadata match),
    /// then launches the async full-text query which may add more results.
    /// </summary>
    private void ApplySearch()
    {
        _queryTerms = TextNormalizer.Normalize(_searchText)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _ftsIds = new HashSet<string>(StringComparer.Ordinal);
        RefreshResults();
        _ = RunFtsQueryAsync(_searchText);
    }

    /// <summary>
    /// Re-applies the filter and updates the status. Resets the per-pass match budget first so the
    /// filter renders at most <see cref="MaxSearchResults"/> tiles — bounding the (non-virtualized)
    /// render cost of each keystroke.
    /// </summary>
    private void RefreshResults()
    {
        _matchBudget = MaxSearchResults;
        ConversationsView.Refresh();
        UpdateSearchStatus();
    }

    /// <summary>Reports how many conversations match, and whether the shown set was capped.</summary>
    private void UpdateSearchStatus()
    {
        if (_queryTerms.Length == 0 && _ftsIds.Count == 0) { UpdateCountStatus(); return; }

        int total = 0;
        foreach (var c in _conversations)
            if (Matches(c)) total++;

        StatusText = total > MaxSearchResults
            ? $"Showing {MaxSearchResults} of {total} matches — refine to see more"
            : $"{total} match{(total == 1 ? "" : "es")}";
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

    private bool _isIndexing;
    /// <summary>True while the full-text index is being (re)built — drives the progress strip's visibility.</summary>
    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetProperty(ref _isIndexing, value);
    }

    private double _indexingProgress;
    /// <summary>Full-text index build progress, 0–100.</summary>
    public double IndexingProgress
    {
        get => _indexingProgress;
        private set => SetProperty(ref _indexingProgress, value);
    }

    private string _indexingText = "";
    /// <summary>Human-readable index progress, e.g. "Indexing full-text search: 42% (296/700)".</summary>
    public string IndexingText
    {
        get => _indexingText;
        private set => SetProperty(ref _indexingText, value);
    }

    private bool _retentionUserSet;
    private int _retentionDays = 1;
    /// <summary>Age threshold (in days) for the "delete older than" maintenance action. Never below 1.</summary>
    public int RetentionDays
    {
        get => _retentionDays;
        set
        {
            _retentionUserSet = true; // a manual change; don't override it on refresh
            SetProperty(ref _retentionDays, Math.Max(1, value)); // clamp: no negatives / no 0
        }
    }

    /// <summary>
    /// Defaults the retention threshold to the age (in whole days) of the oldest conversation, so
    /// "Delete older" would include it. Skipped once the user has set the value manually.
    /// </summary>
    private void ApplyDefaultRetentionDays()
    {
        if (_retentionUserSet || _conversations.Count == 0) return;
        var now = DateTime.UtcNow;
        double maxAgeDays = _conversations.Max(c => (now - c.LastActivityUtc).TotalDays);
        int days = Math.Max(1, (int)Math.Floor(maxAgeDays));
        if (SetProperty(ref _retentionDays, days, nameof(RetentionDays))) { /* notified */ }
    }

    // --- Config inspector (split panel) ---
    private bool _isInspectorOpen;
    /// <summary>Whether the right-hand config inspector panel is shown.</summary>
    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        private set => SetProperty(ref _isInspectorOpen, value);
    }

    private string _inspectorTitle = "";
    /// <summary>Folder whose Claude config is being inspected.</summary>
    public string InspectorTitle
    {
        get => _inspectorTitle;
        private set => SetProperty(ref _inspectorTitle, value);
    }

    /// <summary>Root nodes of the effective-config tree shown in the inspector.</summary>
    public ObservableCollection<ConfigNode> InspectorNodes { get; } = new();

    private double _lastInspectorWidth = 460;
    private GridLength _inspectorColumnWidth = new(0);
    /// <summary>
    /// Width of the inspector column, bound TwoWay so the GridSplitter can resize it and we can
    /// collapse it to 0 on close (which makes the panel disappear entirely).
    /// </summary>
    public GridLength InspectorColumnWidth
    {
        get => _inspectorColumnWidth;
        set => SetProperty(ref _inspectorColumnWidth, value);
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
    public RelayCommand RebuildIndexCommand { get; }
    public RelayCommand InspectSettingsCommand { get; }
    public RelayCommand CloseInspectorCommand { get; }
    public RelayCommand OpenConfigFileCommand { get; }
    public RelayCommand CopyValueCommand { get; }

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

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ApplySearch(); };

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
        RebuildIndexCommand = new RelayCommand(_ => _ = RebuildIndexAsync());
        InspectSettingsCommand = new RelayCommand(InspectSettings);
        CloseInspectorCommand = new RelayCommand(_ => CloseInspector());
        OpenConfigFileCommand = new RelayCommand(
            p => Run(() => OpenPath((p as ConfigNode)?.FilePath ?? p as string)),
            p => (p as ConfigNode)?.CanOpen == true || (p is string s && !string.IsNullOrEmpty(s)));
        CopyValueCommand = new RelayCommand(p => Run(() => { if (p is ConfigNode n) Clipboard.SetText(n.DisplayValue); }));

        _cache.Load();
        _ = LoadAsync();
    }

    private bool FilterPredicate(object obj)
    {
        if (_queryTerms.Length == 0) return true;   // no active query → show everything
        if (obj is not ConversationInfo c) return false;
        if (!Matches(c)) return false;

        // Cap the number of rendered results so a broad query doesn't materialise hundreds of
        // (non-virtualized) tiles per keystroke. The source is recency-desc, so we keep the newest.
        if (_matchBudget <= 0) return false;
        _matchBudget--;
        return true;
    }

    /// <summary>
    /// Whether a conversation matches the current query: every pre-normalised term present in the
    /// metadata blob (computed once per query), or a full-text hit from the background index.
    /// </summary>
    private bool Matches(ConversationInfo c)
    {
        var blob = c.NormalizedSearchBlob;
        foreach (var t in _queryTerms)
        {
            if (!blob.Contains(t, StringComparison.Ordinal))
                return _ftsIds.Contains(c.SessionId);
        }
        return true;
    }

    /// <summary>Runs the full-text query off the UI thread and, if still current, folds the hits into the view.</summary>
    private async Task RunFtsQueryAsync(string query)
    {
        if (!_useFullTextSearch) return; // full-text is opt-in
        var q = query?.Trim() ?? "";
        if (q.Length < 2) return; // too short to be worth a full-text pass

        HashSet<string> ids;
        try { ids = await Task.Run(() => _index.Search(q)); }
        catch { return; }

        // Discard if the user has typed more since this query was issued.
        if (!string.Equals(_searchText.Trim(), q, StringComparison.Ordinal)) return;

        // Only re-filter if the full-text hits actually change the set (avoids a redundant reset).
        if (_ftsIds.SetEquals(ids)) return;

        _ftsIds = ids;
        RefreshResults();
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
                ApplyDefaultRetentionDays(); // default the threshold to the oldest conversation's age
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

        // Build the full-text index only when the feature is enabled.
        if (_useFullTextSearch) StartIndexing();
    }

    /// <summary>Fire-and-forget background index build over the current conversations (used after each scan).</summary>
    private void StartIndexing() => _ = RunIndexBuildAsync(cancelPrevious: true);

    /// <summary>
    /// Runs the full-text index build over the current conversations, reporting progress to
    /// <see cref="IsIndexing"/> / <see cref="IndexingProgress"/> / <see cref="IndexingText"/> on the
    /// UI thread. The heavy work is on a background thread, so the UI stays responsive. Awaitable so
    /// callers (e.g. rebuild) can act on completion. Never throws.
    /// </summary>
    private async Task RunIndexBuildAsync(bool cancelPrevious)
    {
        if (cancelPrevious)
        {
            _indexCts?.Cancel();
            if (_indexTask is not null) { try { await _indexTask; } catch { } }
        }

        _indexCts = new CancellationTokenSource();
        var token = _indexCts.Token;

        var snapshot = _conversations.ToList();
        if (snapshot.Count == 0) { IsIndexing = false; return; }

        // Progress<T> created here captures the UI SynchronizationContext, so the callback is safe.
        var progress = new Progress<(int done, int total)>(p =>
        {
            IndexingProgress = p.total == 0 ? 100 : 100.0 * p.done / p.total;
            IndexingText = $"Indexing full-text search: {(int)IndexingProgress}% ({p.done}/{p.total})";
            IsIndexing = p.done < p.total;
        });

        IsIndexing = true;
        IndexingProgress = 0;
        IndexingText = "Indexing full-text search…";

        _indexTask = _index.BuildAsync(snapshot, progress, token);
        try { await _indexTask; } catch { /* build is best-effort */ }
    }

    /// <summary>
    /// Rebuilds the full-text index from scratch: cancels any running build, closes and deletes the
    /// DB, then re-indexes everything with visible progress, and finally reports a summary.
    /// </summary>
    private async Task RebuildIndexAsync()
    {
        if (_isRebuilding) return;
        _isRebuilding = true;
        try
        {
            // A/B: stop the current build and delete the database (off the UI thread).
            _indexCts?.Cancel();
            if (_indexTask is not null) { try { await _indexTask; } catch { } }
            await Task.Run(() => _index.DeleteDatabase());

            // C: reindex everything from scratch, progress shown on the bottom status bar.
            await RunIndexBuildAsync(cancelPrevious: false);

            // D: summary.
            int convs = _conversations.Count;
            int folders = _conversations
                .Select(c => c.WorkingDirectory)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            MessageBox.Show(
                $"Full-text index rebuilt.\n\n{convs} conversation{(convs == 1 ? "" : "s")} indexed "
                + $"across {folders} folder{(folders == 1 ? "" : "s")}.",
                "Claude Code Explorer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not rebuild the index:\n\n" + ex.Message,
                "Claude Code Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isRebuilding = false;
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

    /// <summary>Opens the config inspector split panel for a folder group (its WorkingDirectory).</summary>
    private void InspectSettings(object? parameter)
    {
        if (parameter is not CollectionViewGroup group) return;
        var folder = group.Name as string ?? "";

        InspectorNodes.Clear();
        foreach (var node in ClaudeConfigInspector.Inspect(folder))
            InspectorNodes.Add(node);

        InspectorTitle = string.IsNullOrWhiteSpace(folder) ? "(unknown folder)" : folder;
        if (InspectorColumnWidth.Value <= 0)
            InspectorColumnWidth = new GridLength(_lastInspectorWidth); // restore last width
        IsInspectorOpen = true;
    }

    private void CloseInspector()
    {
        if (InspectorColumnWidth.IsAbsolute && InspectorColumnWidth.Value > 0)
            _lastInspectorWidth = InspectorColumnWidth.Value; // remember for next open
        InspectorColumnWidth = new GridLength(0);
        IsInspectorOpen = false;
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
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
        _index.Remove(c.SessionId);   // drop it from the full-text index too (best-effort)
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
