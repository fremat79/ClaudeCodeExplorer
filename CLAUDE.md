# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this app is

A WPF (.NET 10, Windows) tray application that scans `~/.claude/projects`, parses each
Claude Code session transcript (`.jsonl`), and presents the conversations as searchable,
project-grouped tiles. Clicking a tile opens Windows Terminal in that conversation's
original working directory and runs `claude --resume <sessionId>`.

## Commands

```powershell
dotnet build                                   # Debug build
dotnet build -c Release                        # Release (DebugType=none)
dotnet run                                      # build + launch (starts hidden in the tray)
dotnet publish -p:PublishProfile=FolderProfile  # single-file, self-contained, win-x64, R2R
```

There is no test project and no linter configured — build is the only gate.

## Architecture

MVVM, but hand-rolled (no MVVM framework). The single window binds to `MainViewModel`;
all real work lives in stateless `static` service classes under `Services/`.

**Data flow (read path):**
`ProjectScanner.Scan` enumerates `~/.claude/projects/<encoded-path>/*.jsonl` →
for each file, `CacheService.TryGet` returns a cached `ConversationInfo` if the file's
last-write-time-ticks **and** size are unchanged, otherwise `JsonlParser.Parse` reads it →
results sorted by `LastActivityUtc` desc → surfaced through `MainViewModel.ConversationsView`
(an `ICollectionView` that filters by `SearchBlob` and groups by `WorkingDirectory`).

Scanning runs on a background thread (`Task.Run`) and parses files in parallel
(`Parallel.ForEach`). The parse result cache is persisted to
`%LOCALAPPDATA%\ClaudeCodeExplorer\cache.json`; `ConversationInfo` must stay
System.Text.Json-serializable because it *is* the cache record (note the `[JsonIgnore]`
`SearchBlob`).

**Data flow (actions):** `MainViewModel` commands delegate to `TerminalLauncher`
(resume / open terminal / open in Explorer) and to local `Delete`. Everything the user
triggers is wrapped in `Run(...)` which shows exceptions as a `MessageBox`.

**Transcript parsing (`JsonlParser`) — the domain-specific core:**
- Each `.jsonl` line is an independent JSON object; malformed / half-written lines are
  skipped, never fatal (files may be mid-write by a live Claude Code session).
- Only `type == "user" | "assistant"` lines count as messages; `type == "summary"` lines
  carry Claude's own conversation titles, keyed by `leafUuid`.
- **Title precedence:** summary matching the final message's `uuid` → most recent summary →
  first "real" user message → `"(no messages)"`. First-user-message detection skips `isMeta`
  lines and text starting with `<command-`, `<local-command`, or `Caveat:`.
- `WorkingDirectory` / `GitBranch` come from the `cwd` / `gitBranch` fields inside the
  transcript, not from the folder name. The `~/.claude/projects` folder name is a *flattened
  encoding* of the path and is only a fallback.

**Resume mechanics (`TerminalLauncher`):** `claude --resume <id>` only resolves a session
when launched from its original working directory (the path is encoded into the storage
folder name). If that directory was deleted, `Resume` recreates it (empty) so the path
matches again; if even that fails it opens a terminal in the home folder and explains why.
`wt.exe` is preferred, with a plain `powershell.exe` fallback.

**Delete:** removes the `.jsonl` from disk, then `TryRemoveEmptyProjectFolder` deletes the
now-empty storage folder — but *only* if it sits directly under `ProjectScanner.ProjectsRoot`
(guard against deleting anything else).

## Tray & lifecycle (`App.xaml.cs`)

App.xaml has **no `StartupUri`** — `App.OnStartup` builds the window manually and the app
lives in the system tray:
- `ShutdownMode = OnExplicitShutdown`; closing or minimizing the window **hides to tray**
  rather than exiting (`OnWindowClosing` cancels close, `_exiting` flag gates real shutdown).
- Autostart is a per-user `...\CurrentVersion\Run` registry value (`StartupService`),
  offered once via `SettingsService` first-run flags; the `--tray` arg is passed on autostart.
- `ThemeManager` picks Light/Dark from `AppsUseLightTheme` in the registry and swaps the
  merged `Themes/Light.xaml` / `Themes/Dark.xaml` dictionary live on `UserPreferenceChanged`.
  All colors in XAML are `DynamicResource` brush keys defined in those two dictionaries.

## Gotchas

- **WinForms is enabled only for the tray icon** (`NotifyIcon`). The `.csproj` deliberately
  removes the `System.Windows.Forms` and `System.Drawing` implicit usings so bare names like
  `Application`, `MessageBox`, `WindowState` resolve to **WPF**. Reference WinForms via the
  `WinForms` alias (`using WinForms = System.Windows.Forms;`) and fully-qualified
  `System.Drawing.*` types — do not add those namespaces to global usings.
- Nullable and ImplicitUsings are both `enable`.
