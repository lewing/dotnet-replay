# Project Context

- **Owner:** Larry Ewing
- **Project:** dotnet-replay — Interactive terminal viewer for Copilot CLI sessions and waza evaluation transcripts
- **Stack:** C# / .NET 10, single-file app (replay.cs ~180KB), Spectre.Console, Markdig, Microsoft.Data.Sqlite
- **Repository:** https://github.com/lewing/dotnet-replay
- **Created:** 2026-02-24

## Key Implementation Details

- All code lives in `replay.cs` — single-file .NET 10 app
- NuGet package metadata: #:property directives at top of file
- Dependencies: Spectre.Console 0.49.1, Markdig 0.40.0, Microsoft.Data.Sqlite 9.0.3
- Record types used for JSON output: JsonlTurnRecord, JsonlToolRecord, SummaryRecord, etc.
- CLI parsing: switch-based at top of main body
- Stats command has its own parser section
- Session browser: `BrowseSessions` loads from ~/.copilot/session-state/ and ~/.claude/projects/
- SQLite support: reads session-store.db for richer session data with fallback to file enumeration
- Interactive pager: custom rendering loop with Spectre.Console

## Project History

- v0.5.0: JSON/summary output (issue #5), record type refactor
- v0.5.1: Session ID fix (issue #7)
- v0.5.2: skills_invoked fix (issue #9)
- v0.5.3: stats command (issue #13)
- Current: SQLite session-store.db support on evalj branch

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2025-02-24: DB polling and external DB support
- **Feature 1:** Added periodic DB re-query in browser mode. After initial session load completes, the scan thread now polls the SQLite DB every 5 seconds for new sessions. Uses `HashSet<string>` to track known session IDs and avoid duplicates. Only re-queries DB; filesystem and Claude Code scans remain one-shot. Polling continues until thread is cancelled (background thread).
- **Feature 2:** Added `--db <path>` CLI argument to load sessions from an external session-store.db file. CLI parsing treats .db files as dbPath (not filePath). `LoadSessionsFromDb` and `BrowseSessions` now accept optional `dbPathOverride` parameter. When external DB is used, filesystem fallback scan is skipped.
- Both features integrate with existing `sessionsLock` and UI throttle loop — no changes needed to rendering logic.
- Pattern: Use background thread polling with `Thread.Sleep(5000)` for periodic updates in TUI apps.
