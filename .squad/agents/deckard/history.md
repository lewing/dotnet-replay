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

### XenoAtom UI Implementation (2026-03-13) — COMPLETE WITH PHASE 1 DELIVERY

**Feature Parity Assessment (Published in decisions.md):**
- XenoAtom pager (XenoPager.cs, 322 LOC) implements: filter cycling (f), tool expansion toggle (t), info overlay (i), search (/), follow mode, quit (q/Esc), browse (b), resume (r)
- XenoAtom browser (XenoSessionBrowser.cs, 776 LOC) implements: DataGrid with sortable columns, live DB polling, session search (Ctrl+F), preview panel, multi-source loading (Copilot/Claude/SkillValidator), resume capability
- Spectre pager has superior navigation: Page Up/Down, Home/End, vim-style (h/j/k/l), search match navigation (n/N), horizontal scrolling, line numbers in status
- Spectre browser has preview feature that loads & parses JSONL content; XenoAtom browser shows raw JSONL preview instead of rendered turn content

**Quality Assessment:**
- Build succeeds with 0 warnings (good discipline)
- Both implementations follow parameter injection pattern consistently
- No TODOs, FIXMEs, or commented-out code detected
- Clean command registration pattern in XenoPager using KeyGesture
- XenoSessionBrowser correctly handles multi-DB schema (Copilot CLI vs SkillValidator) with lazy detection

**Key Design Gaps (Prioritized as 3-Phase Rollout):**

**Phase 1 (DELIVERED):**
1. ✅ **Pager Navigation**: Implemented Page Up/Down, Home/End, vim-style j/k/g/G via reflection bridge to LogControl's `_scrollViewer` (tech debt: replace if LogControl API matures)
2. ✅ **Search Match Navigation**: Implemented n/N using public GoToNextMatch/GoToPreviousMatch APIs; status bar shows "Match X/Y"
3. ✅ **Preview Feature**: Replaced raw JSONL with DataParsers + ContentRenderer.RenderJsonlContentLines(); limited to 50 turns

**Phase 2 (Planned):**
4. **Horizontal Scrolling**: XenoPager does not expose horizontal scroll (Spectre uses ← →/h l and has a status indicator)
5. **Enhanced Status Bar**: Show "Line X of Y", search match count context

**Phase 3 (Planned):**
6. **Regression Tests**: XenoAtom-specific test coverage

**Technical Debt Notes:**
- Pager scroll bridge uses reflection on LogControl's internal `_scrollViewer` field; flagged for replacement
- XenoPager status bar can be enhanced with positional + search context
- Performance impact of preview parsing on large sessions (>50MB) verified acceptable in Phase 1 delivery

**Patterns to Preserve:**
- XenoPager's follow mode matches Spectre's file-watching + incremental parse logic
- Both browsers use background thread for session loading with thread-safe lock
- Filter cycling logic is identical (all → user → assistant → tool → error)
