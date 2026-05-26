using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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

    public MainViewModel()
    {
        ConversationsView = CollectionViewSource.GetDefaultView(_conversations);
        ConversationsView.SortDescriptions.Add(
            new SortDescription(nameof(ConversationInfo.LastActivityUtc), ListSortDirection.Descending));
        ConversationsView.Filter = FilterPredicate;

        RefreshCommand = new RelayCommand(_ => _ = LoadAsync());
        ResumeCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.Resume(c); }));
        OpenTerminalCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.OpenTerminal(c); }));
        OpenFolderCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) TerminalLauncher.OpenInExplorer(c); }));
        CopyIdCommand = new RelayCommand(p => Run(() => { if (p is ConversationInfo c) Clipboard.SetText(c.SessionId); }));

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
