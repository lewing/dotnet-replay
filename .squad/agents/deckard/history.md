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

- replay.cs is now **4,542 lines** (not ~3,300 as previously documented). Growth mostly in eval processing, session browser, and stats.
- .NET 10 file-based apps do NOT support a `#:file` directive — they are strictly single-file by design.
- .NET 10 file-based apps DO support `#:project ../lib/Lib.csproj` to reference class library projects. This is the official decomposition path.
- All functions in replay.cs are **local functions** capturing top-level variables (`noColor`, `expandTools`, `markdownPipeline`, etc.). Splitting requires bundling these into a shared options type.
- The three largest extractable sections: Content Rendering (962 lines), Session Browser (696 lines), Markdown Rendering (598 lines).
- Tests invoke `dotnet run replay.cs` via subprocess — a `#:project` split should be transparent to them as long as replay.cs remains the entry point.
- `dotnet pack replay.cs` produces the NuGet global tool — need to verify `#:project` compatibility with pack before committing to the split.
