# Claude Code Explorer

A lightweight **Windows tray app** that turns your [Claude Code](https://claude.com/claude-code)
history into a searchable, project-grouped dashboard. It scans `~/.claude/projects`, parses every
session transcript, and shows your conversations as tiles. Click one to reopen it — the app
launches Windows Terminal in that conversation's original working directory and runs
`claude --resume <sessionId>`.

Everything runs **locally**. No network calls, no telemetry — it only reads the transcript files
Claude Code already writes on your machine.

> Built with WPF on **.NET 10** (Windows only).

---

## Table of contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Usage](#usage)
- [Search](#search)
- [Config inspector](#config-inspector)
- [Maintenance & cleanup](#maintenance--cleanup)
- [Where data is stored](#where-data-is-stored)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Building & publishing](#building--publishing)
- [Contributing](#contributing)
- [Privacy](#privacy)
- [License](#license)

---

## Features

- **System-tray app** — starts hidden in the notification area; closing/minimizing hides to tray
  instead of quitting. Optional *start with Windows*.
- **Conversation tiles**, grouped by project folder (collapsible), sorted by most recent activity.
  Each tile shows the session **name** (your `/rename` title, or the session GUID), a **preview**
  of the first message, the relative time, and the message count.
- **One-click resume** — opens Windows Terminal in the conversation's working directory and runs
  `claude --resume`. Also: open a plain terminal there, open the folder in Explorer, or copy the
  session id (right-click a tile).
- **Fast search** with multi-word AND matching and accent-insensitivity — plus an optional
  **full-text mode** that searches inside the entire conversation body (local SQLite index).
- **Config inspector** — a devtools-style split view showing the *effective* Claude Code
  configuration for a folder, with clear override highlighting.
- **Maintenance actions** — delete empty conversations, delete conversations older than N days, or
  multi-select and batch-delete across folders (always behind a confirmation).
- **Light / dark theme** that follows the Windows system setting live.

See [`WHATS-NEW.md`](WHATS-NEW.md) for the latest additions.

---

## Requirements

- **Windows** (10/11).
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** to build/run from source (a published
  build is self-contained and needs no separate runtime install).
- **[Claude Code](https://claude.com/claude-code)** installed and used at least once (so
  `~/.claude/projects` exists). The `claude` CLI must be on your `PATH` for *Resume* to work.
- **Windows Terminal** (`wt.exe`) recommended; the app falls back to `powershell.exe` if it isn't
  installed.

---

## Getting started

Clone and run:

```powershell
git clone https://github.com/fremat79/ClaudeCodeExplorer.git
cd ClaudeCodeExplorer
dotnet run
```

The app starts **in the system tray** (no window pops up). **Double-click the tray icon** to open
the main window. On first launch it offers to start automatically with Windows.

---

## Usage

- **Open the window**: double-click the tray icon (or right-click → *Open*).
- **Resume a conversation**: click its tile. Windows Terminal opens in the conversation's original
  folder and runs `claude --resume <sessionId>`.
- **Tile right-click menu**: Resume · Open terminal here (no resume) · Open folder in Explorer ·
  Copy session id · Delete.
- **Folder header**: *Select all* (checkboxes), *Inspect settings* (opens the config split view),
  or right-click the folder for the same actions.
- **Refresh**: rescans `~/.claude/projects` (button in the bottom status bar).
- **Exit**: right-click the tray icon → *Exit* (closing the window only hides it).

> If a conversation's original folder was deleted, *Resume* recreates the (empty) path so
> `claude --resume` can still resolve the session; if even that fails it opens a terminal in your
> home folder and explains why.

---

## Search

The search box (top toolbar) filters tiles as you type.

- **Metadata search** (always on): matches the session name, first prompt, project name, working
  directory, git branch and session id. It is **multi-word AND** (every term must match, in any
  order) and **accent/case-insensitive** (`citta` matches `Città`).
- **Full-text search** (optional): tick **“Full-text search”** in the bottom status bar to *also*
  match inside the **entire conversation body**.

### How full-text works

Searching the full body of hundreds of multi-MB transcripts on every keystroke would be far too
slow, so the app keeps a local **SQLite FTS5 index** (`search.db`). This index is **built and
maintained automatically in the background** — incrementally, so only new or changed conversations
are re-read, and a progress bar in the status bar shows a full (re)build. The **checkbox is just a
switch**: it decides whether your queries consult that index; toggling it never triggers heavy work.

You can force a clean rebuild any time with **Rebuild index** (closes, deletes and rebuilds the
index from scratch, then reports how many conversations across how many folders were indexed).

---

## Config inspector

Right-click a folder (or use its **Inspect settings** button) to split the view: tiles on the left,
a tree of the **effective Claude Code configuration** on the right — like the browser devtools
"computed" panel.

- Merges every applicable `settings.json` tier in precedence order — **user**, user-local, **project**
  (`.claude/settings.json`), **project-local** (`.claude/settings.local.json`) and **managed** — and
  shows the effective value of each setting.
- **Overridden settings are flagged** (with a tooltip showing what they override), and **coloured
  source badges** (user / project / local / managed) tell you where each value comes from.
- Also surfaces related config: **MCP servers** (`.mcp.json`), applicable **CLAUDE.md** files, the
  **`.claude/` folder contents** (agents, commands, skills, rules, hooks, output-styles), and the
  **source files** with their paths.
- Panels are **resizable** (drag the divider), the panel disappears when closed, and a **single
  click on a node opens the file** that defines it.

---

## Maintenance & cleanup

All destructive actions live in the bottom status bar and ask for confirmation (showing the count
and the affected folders):

- **Delete empty** — conversations with no messages.
- **Delete older than N days** — the day spinner defaults **dynamically** to the age of your oldest
  conversation (minimum 1 day), so the action is immediately meaningful.
- **Delete selected** — check tiles (per-tile checkbox, or *Select all* per folder) and delete them
  in one go, across different folders. *Clear selection* unchecks everything.

Deleting a conversation removes its `.jsonl` from disk, prunes the now-empty
`~/.claude/projects/<encoded>` storage folder, and removes it from the search index.

---

## Where data is stored

The app keeps its own state under `%LOCALAPPDATA%\ClaudeCodeExplorer\`:

| File | Purpose |
|------|---------|
| `cache.v2.json` | Parsed-conversation cache (keyed on each file's write-time + size) for fast startup. |
| `search.db` | Local SQLite FTS5 full-text index (plus `-wal` / `-shm` sidecars while open). |
| `settings.json` | App preferences (first-run flags, autostart prompt, full-text toggle). |

All of these are **regenerable** — delete the folder and the app rebuilds cache and index on next
launch (this also resets the first-run state). Autostart is a per-user
`…\CurrentVersion\Run` registry value, *not* stored in this folder.

---

## How it works

- **Scanning** enumerates `~/.claude/projects/<encoded-path>/*.jsonl` on a background thread and
  parses files in parallel, reusing the cache for unchanged files.
- **Transcript parsing** reads each `.jsonl` line as an independent JSON object (malformed / partly
  written lines are skipped, never fatal). Titles come from Claude's own `summary` lines or the
  `/rename` custom title; the working directory and git branch come from the `cwd` / `gitBranch`
  fields *inside* the transcript.
- **Resume** relies on the fact that `claude --resume <id>` only resolves a session from its
  original working directory, which is encoded into the storage folder name.

For a deeper architectural overview, see [`CLAUDE.md`](CLAUDE.md).

---

## Project structure

```
ClaudeCodeExplorer/
├─ App.xaml / App.xaml.cs        # Tray lifecycle, theme init, autostart prompt
├─ MainWindow.xaml(.cs)          # UI: tiles, search, status bar, config inspector
├─ Models/
│  ├─ ConversationInfo.cs        # One conversation (also the cache record)
│  └─ ConfigNode.cs              # Config-inspector tree node
├─ ViewModels/                   # MainViewModel + hand-rolled MVVM helpers
├─ Services/
│  ├─ ProjectScanner.cs          # Enumerates ~/.claude/projects
│  ├─ JsonlParser.cs             # Parses transcripts (metadata + full text)
│  ├─ CacheService.cs            # On-disk parse cache
│  ├─ SearchIndexService.cs      # SQLite FTS5 index (build / query / rebuild)
│  ├─ ClaudeConfigInspector.cs   # Merges settings.json tiers → tree
│  ├─ TerminalLauncher.cs        # Opens Windows Terminal / resume
│  ├─ ThemeManager.cs            # Light/dark from the OS
│  ├─ SettingsService.cs         # App preferences
│  └─ StartupService.cs          # Autostart registry entry
├─ Converters/ · Behaviors/      # WPF value converters & attached behaviors
├─ Themes/                       # Light.xaml / Dark.xaml brush dictionaries
└─ Assets/                       # Tray icon
```

Architecture is **hand-rolled MVVM** (no MVVM framework): the window binds to `MainViewModel`, and
all real work lives in stateless `static` services. The only runtime dependency is
**`Microsoft.Data.Sqlite`** (bundled native SQLite with FTS5).

---

## Building & publishing

```powershell
dotnet build                                    # Debug build
dotnet build -c Release                         # Release
dotnet run                                       # build + launch (starts in the tray)
dotnet publish -p:PublishProfile=FolderProfile   # single-file, self-contained, win-x64, R2R
```

The publish profile produces a single-file, self-contained executable for `win-x64` (bundling the
.NET runtime, so no separate runtime install is needed on the target machine; the native
`e_sqlite3.dll` is placed next to the `.exe`). There is no test project — the build is the only gate.

---

## Contributing

The repository is public so you can browse, clone and fork it. **Nobody can push directly** — the
only way to propose a change is a **fork + pull request**, which the maintainer chooses whether to
merge. Issues and PRs are welcome, but there's no obligation to accept them.

---

## Privacy

Claude Code Explorer is fully **local**: it only reads the transcript and settings files that
Claude Code already stores under `~/.claude`, and writes its own cache/index/preferences under
`%LOCALAPPDATA%`. It makes **no network requests** and collects **no telemetry**.

---

## License

No license has been chosen yet — until one is added, the code is "all rights reserved" by default.
If you intend this to be open source, add a `LICENSE` file (e.g. MIT) and reference it here.
