# Project Context

- **Owner:** Larry Ewing
- **Project:** dotnet-replay — Interactive terminal viewer for Copilot CLI sessions and waza evaluation transcripts
- **Stack:** C# / .NET 10, single-file app (replay.cs ~180KB), Spectre.Console, Markdig, Microsoft.Data.Sqlite
- **Repository:** https://github.com/lewing/dotnet-replay
- **Created:** 2026-02-24

## Key Architecture

- Single-file .NET 10 app: all code in `replay.cs` (~3300 lines)
- NuGet global tool: `dotnet-replay`, command name `replay`
- CLI parsing: switch-based in main body
- Event processing: supports Copilot CLI JSONL, Claude Code JSONL, waza JSON, SQLite session-store.db
- TUI: Spectre.Console interactive pager with vim-style keybindings
- Output modes: interactive (default), stream, JSON (--json), summary (--summary)
- Stats command: batch analysis across transcripts
- Tests: subprocess-based in `tests/` (35 tests across 5 test files)

## Project History

- Born as a side-project in the Arena repo (Copilot CLI skills proving ground)
- Originally built by The Usual Suspects squad (McManus, Fenster, Hockney, Keaton, Kobayashi)
- v0.5.0: JSON/summary output with record types (refactored from anonymous types)
- v0.5.1: Session ID bugfix in --summary
- v0.5.2: skills_invoked fix in summary output
- v0.5.3: stats command for batch analysis
- Current: SQLite session-store.db support (evalj branch, unreleased)

## Open Issues

- #11: Add session diff mode
- #12: Add grep/search across transcripts

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->
