# What's New

A summary of the features added in this development session.

## Conversation tiles

- **Session name on each tile.** The tile's small accent heading now shows the session's
  **name** instead of the (redundant) project folder — which is already the group header.
  - If the session was renamed with Claude Code's `/rename`, the custom title is shown.
  - Otherwise the tile shows the **session GUID** as an identifier.
- **Conversation extract in the body.** The larger line always shows a preview of the first
  user message, so unnamed sessions are no longer "empty-looking" and the heading never just
  duplicates the body text.

## Maintenance: bulk deletion

Two rule-based cleanup actions (with confirmation dialogs that list how many conversations and
which folders are affected):

- **Delete empty** — removes conversations that have no messages.
- **Delete older than N days** — removes conversations whose last activity is older than the
  threshold set with the day spinner.
  - Minimum is **1 day** (no zero/negatives).
  - The default is **dynamic**: it is set to the age (in whole days) of your **oldest**
    conversation, so the action is immediately meaningful (instead of a fixed value that might
    match nothing). Manual changes are preserved across refreshes.

## Batch selection & delete

- A **checkbox** on every tile to mark conversations.
- **Select all** per folder (toggles the whole group).
- **Clear selection** (global).
- **Delete selected** — deletes every checked conversation, **across different folders**, after a
  confirmation showing the count and the affected folders. The button is **enabled only when at
  least one conversation is selected**.

## Search

- **Smarter in-memory search** over the conversation metadata (title, name, first prompt, path,
  branch, id):
  - **Multi-word AND** — all typed terms must match (order-independent).
  - **Accent- and case-insensitive** (e.g. `citta` matches `città`).
- **Full-text search (opt-in).** A **"Full-text search" checkbox** in the status bar (off by
  default, **remembered across restarts**). When enabled, search also looks **inside the whole
  conversation body** using a local **SQLite FTS5** index.
  - The index is built **in the background** so the UI stays responsive; a **progress bar** in
    the status bar shows how far along it is.
  - The index updates **incrementally** (only new/changed conversations are re-read), and deleted
    conversations are removed from it.
  - **Rebuild index** button — closes, deletes and rebuilds the index from scratch (with progress),
    then reports how many conversations across how many folders were indexed.
  - Everything is **local** — no server, a single `search.db` file under `%LOCALAPPDATA%`.
- **Responsiveness.** Typing is debounced, the query is normalised once (not per item), and the
  number of rendered results is capped so a broad query never stalls the UI (a "showing N of M"
  hint appears when capped).

## Layout

- The **top toolbar is now dedicated to search** only.
- **All actions moved to a docked bottom status bar**: the day spinner, Delete older / Delete
  empty / Delete selected / Clear selection / Rebuild index / Refresh, the full-text toggle, plus
  the status text and the indexing progress bar.

## Config inspector (settings devtools)

Right-click a folder (or use its **"Inspect settings"** action) to **split the view**: tiles on
the left, and on the right a **tree of the effective Claude Code configuration** for that folder —
similar to the browser devtools "computed" view.

- Merges all applicable `settings.json` tiers in precedence order — **user**, user-local, project
  (`.claude/settings.json`), project-local (`.claude/settings.local.json`), and **managed** — and
  shows the **effective** value of each setting.
- **Override highlighting**: settings shadowed by a higher-precedence tier are clearly marked, with
  a tooltip showing what they override. **Coloured source badges** (user / project / local /
  managed) tell you where each value comes from. Permission arrays are shown unioned across tiers.
- Also surfaces related config: **MCP servers** (`.mcp.json`), applicable **CLAUDE.md** files,
  the **`.claude/` folder contents** (agents, commands, skills, rules, hooks, output-styles), and
  the list of **source files** with their paths and presence.
- **Resizable** panels (drag the divider), the panel **fully disappears** when closed, and a
  **single click on any node opens the file** that defines it.
