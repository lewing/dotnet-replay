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

### 2026-03-15: PR #149 rubric persistence and judge-mode validation
- `eng/skill-validator` rejudge fidelity depends on persisting scenario rubrics from `eval.yaml`; storing a JSON `string[]` rubric snapshot per session lets `rejudge` reconstruct `EvalScenario` with the original rubric instead of silently falling back to the default judge rubric.
- Store `[]` for scenarios that intentionally have no rubric, and reserve `NULL` rubric values for legacy rows; that makes rejudge warnings meaningful because `NULL` now signals an older `sessions.db` that predates rubric persistence.
- `SessionDatabase` should self-migrate older validator databases on open by adding the missing `sessions.rubric` column and bumping `schema_info["version"]`, so `GetCompletedSessions()` can safely query both fresh and legacy result directories.
- `System.CommandLine` 2.0.4 uses `AcceptOnlyFromAmong(...)` rather than `FromAmong(...)` to reject invalid option values like mistyped `--judge-mode` arguments.

### 2026-03-15: XenoSessionBrowser preview pane collapse
- In `XenoSessionBrowser.cs`, toggling a preview pane with `IsVisible` alone is not enough to restore the session list to full width inside the XenoAtom `HStack`; the hidden pane can still hold layout space.
- To fully collapse the preview and let the `DataGrid` expand again, change the preview pane's width constraint when toggling (`MaxWidth = 0` when hidden, restore the fixed pane width when shown).
- When hiding the preview, also invalidate any in-flight preview generation and clear pending preview text so stale worker output does not repopulate a collapsed pane.

### 2026-03-15: PR #149 review fixes in skill-validator
- `eng/skill-validator` rejudge runs cannot infer the original judge from `sessions.model`; the durable source is `schema_info["judge_model"]`, and rejudge should require `--judge-model` when that metadata is missing.
- Rejudge judge sessions must use fresh temporary working directories instead of persisted run `WorkDir` values, because `ValidateCommand` always cleans agent work dirs via `AgentRunner.CleanupWorkDirs()` even when `--keep-sessions` preserves the session artifacts.
- `SessionDatabase.ComputeDirectorySha()` must frame each relative path and file payload with explicit lengths; concatenating raw path bytes and file bytes makes distinct directory layouts collide.

### 2026-03-14: XenoPager Phase 2 complete — horizontal scroll & status bar
- Horizontal scrolling implemented with `h`/`l`/`0` keys (left/right/reset) using binding-safe `trackedHorizontalOffset` pattern, matching the existing `trackedOffset` vertical scroll workaround.
- Status bar header now displays: line position (e.g., "Line 45/123"), column indicator ("Col X+"), search match count ("Match 3/15" in search mode), filter status, and follow mode toggle state.
- Pattern applied: All offset updates go through local tracked values; `SetHorizontalOffset()` / `SetVerticalOffset()` are the only write points to `ScrollViewer` properties, avoiding binding-tracker read/write conflicts.
- Horizontal offset preserved across `PopulateLog()` rebuilds triggered by filter changes, follow updates, and tool toggles.
- Build clean, all feature-gap tests passing.

### 2026-03-14: XenoPager Phase 2 horizontal/status pattern
- `XenoPager.cs` should treat both `ScrollViewer.VerticalOffset` and `ScrollViewer.HorizontalOffset` as write-only inside the XenoAtom UI loop. Keep local tracked values (`trackedOffset`, `trackedHorizontalOffset`) and drive the reflected `LogControl._scrollViewer` from helpers to avoid binding-tracker read/write conflicts.
- `PopulateLog()` should reapply the tracked horizontal offset after rebuilding lines so filter changes, follow updates, and tool toggles preserve sideways scroll state.
- The compact Xeno pager status/header now lives in `XenoPager.cs` and should surface line position, optional `Col X+`, and search match counts, while `InteractivePager.cs` remains the parity reference for vim-style pager behavior.
- Key file paths for this work: `XenoPager.cs` (XenoAtom pager implementation) and `InteractivePager.cs` (Spectre parity reference).

### 2025-02-24: DB polling and external DB support
- **Feature 1:** Added periodic DB re-query in browser mode. After initial session load completes, the scan thread now polls the SQLite DB every 5 seconds for new sessions. Uses `HashSet<string>` to track known session IDs and avoid duplicates. Only re-queries DB; filesystem and Claude Code scans remain one-shot. Polling continues until thread is cancelled (background thread).
- **Feature 2:** Added `--db <path>` CLI argument to load sessions from an external session-store.db file. CLI parsing treats .db files as dbPath (not filePath). `LoadSessionsFromDb` and `BrowseSessions` now accept optional `dbPathOverride` parameter. When external DB is used, filesystem fallback scan is skipped.
- Both features integrate with existing `sessionsLock` and UI throttle loop — no changes needed to rendering logic.
- Pattern: Use background thread polling with `Thread.Sleep(5000)` for periodic updates in TUI apps.

### 2026-03-13: XenoAtom Phase 1 Assessment (Deckard Assessment Input)
- Deckard completed feature parity assessment. XenoAtom.Terminal.UI has 6 identified gaps: 3 critical (navigation, search cycling, preview), 3 polish (horizontal scroll, status bar, line numbers).
- Key blocker: LogControl v1.3.0 API lacks public scroll methods; may require reflection bridge for Phase 1.
- Batty tasked with Phase 1 implementation (Est. 4–7 hours for 3 items).
- Questions raised: LogControl scroll API, search integration feasibility, horizontal scroll support, performance impact on large sessions.

### 2026-03-13: XenoAtom pager/browser phase 1
- XenoAtom.Terminal.UI `LogControl` 1.3.0 exposes search state and search navigation (`OpenSearch`, `Search`, `SearchText`, `MatchCount`, `ActiveMatchIndex`, `GoToNextMatch`, `GoToPreviousMatch`) plus `ScrollToTail`, but it does not expose public page/line/home/end scroll methods.
- Xeno pager search parity should use the public `GoToNextMatch`/`GoToPreviousMatch` APIs for `n`/`N` instead of reimplementing match tracking.
- Xeno pager scrolling currently has to bridge through LogControl's internal `_scrollViewer` and drive its public `VerticalOffset`/`ViewportHeight` values to implement PageUp/PageDown/Home/End and vim-style `j`/`k`/`g`/`G`.
- Xeno session previews should parse events with `DataParsers`, render with `ContentRenderer.RenderJsonlContentLines`, and strip Spectre markup before pushing lines into `TextBlock` controls. Keeping the preview to roughly the first 6 turns / 28 lines keeps the browser readable.

### 2026-03-14: Review fixes for Xeno follow/preview
- `JsonDocument` instances created only for follow-mode updates must not be retained in `JsonlData.Events`; clone the `JsonElement` needed for `Turns`, dispose the document immediately, and increment `EventCount` separately.
- Xeno session browser preview workers should share a generation counter and bail out before expensive file parsing when their generation is stale; the UI-side generation check alone is not enough to prevent wasted background work.
- Skill DB preview metrics parsing should use `using var` for temporary `JsonDocument` instances inside `BuildPreviewText()`.

### 2026-03-15: Checkpoint Navigation (Tier 1)
- Added `CheckpointRow` record to `Models.cs` and `LoadCheckpointsForSession` + `ResolveSessionDbContext` static methods to `DataParsers.cs`.
- `ResolveSessionDbContext` derives the `session-store/sessions.db` path and session ID from an `events.jsonl` file path by walking the directory structure (`session-state/{id}/events.jsonl` → `session-store/sessions.db`).
- Both pagers (`InteractivePager.cs` and `XenoPager.cs`) add a `c` keybinding for checkpoint navigation: list view with j/k selection, Enter to view detail (overview, work_done, next_steps), Escape to navigate back.
- Graceful degradation: if the checkpoints table doesn't exist, session has no checkpoints, or DB path can't be resolved, the feature silently no-ops.
- XenoPager checkpoint state must be declared before any `AddCommand` closures that capture it (C# local variable forward-reference restriction).
- Fixed pre-existing test bug: `InsertCheckpoint` helper in `CheckpointTests.cs` was missing `cmd.ExecuteNonQuery()`.

### 2026-04-02: Checkpoint Navigation Complete & Tested (Tier 1 Delivered)
- Tier 1 complete: Both pagers have working checkpoint navigation with `c` keybinding
- Database integration patterns proven: parameter injection + graceful degradation working correctly
- Ready to unblock Tier 2 (Full-Text Search) after adoption feedback
- Build clean (0 warnings), 84 tests passing, all CP tests green

### 2026-04-02: XenoSessionBrowser DataGrid sort fix
- `DataGridListDocument.AddRow()` appends rows in insertion order; the DataGrid displays them in that order regardless of any backing list sort.
- `DataGridDocumentView.SetSortDescriptions` exists but only sorts by column string values — unsuitable for human-readable age strings like "2h", "3d".
- Fix: on each UI frame where new batches arrive, clear the DataGrid (`RemoveRows`) and rebuild from the already-sorted `allSessions` list snapshot. This guarantees newest-first order across all loading paths (DB, filesystem, Claude, polling).
- `DataGridListDocument` also exposes `InsertRow(int index, T row)` for positional insertion if needed in the future.

### 2026-04-02: Tier 2 — Full-Text Search Across Sessions
- Added `SearchResult` record to `Models.cs` and `SearchSessions` static method to `DataParsers.cs` using FTS5 `snippet()` with `»`/`«` highlight markers and `MATCH @query ORDER BY rank` for relevance sorting.
- `SearchSessions` follows the same graceful degradation pattern as `LoadCheckpointsForSession`: empty query → empty list, missing table → caught exception → empty list, nullable `SourceId` handled.
- XenoSessionBrowser (`S` key): TextBox-based search input panel, results populate the DataGrid replacing session rows, Escape restores session list. Commands registered on `searchInput` for Enter/Escape to handle submit/cancel while TextBox has implicit keyboard focus.
- SessionBrowser/Spectre (`s` key): Uses `AnsiConsole.Ask<string>()` for query input, shows results in a vim-navigable overlay (j/k/Enter/Esc), selecting a result returns its `EventsPath` to the pager loop.
- InteractivePager (`s` key): FTS search overlay using `ResolveSessionDbContext` to derive DB path from the current events.jsonl, read-only result display.
- XenoPager (`s` key): Reuses LogControl's built-in search bar for query text capture — first `s` opens search bar, user types query, second `s` executes FTS search. Results shown in log with j/k navigation.
- DB path resolution: tries `session-store.db` first, then `session-store/sessions.db` as fallback. Both browsers and pagers share the same pattern.
- 20 unit tests in `SearchTests.cs` covering: happy path, highlight markers, empty/whitespace/null queries, missing table, empty table, limit, cross-session matches, source types, nullable SourceId, prefix/phrase/boolean/malformed queries.
