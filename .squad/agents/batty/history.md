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

### 2025-07-22: Convert from file-based app to .csproj project
- **Created `dotnet-replay.csproj`**: Migrated all `#:property` and `#:package` directives from replay.cs into a standard .csproj with `<PackAsTool>`, `<ToolCommandName>`, package references, and `<Compile Remove>` for tests/ and .squad/ directories.
- **Extracted `Models.cs`**: Moved all record types (JsonlData, WazaData, EvalCaseResult, EvalData, PagerAction, TurnOutput, TurnCounts, TokenCounts, ValidationOutput, SessionSummary, WazaSummary, FileStats) to a separate file. These had no captured state dependencies — cleanest extraction target.
- **Updated test project**: Removed `<Compile Include="..\replay.cs">` and `<Features>FileBasedProgram</Features>` from ReplayTests.csproj. Changed all test files to use `dotnet run --project {csproj}` instead of `dotnet run {replay.cs}`.
- **Key constraint preserved**: All local functions in replay.cs capture top-level variables (noColor, expandTools, full, filterType, tail, markdownPipeline, etc.). Extracting them to classes requires threading a shared options object through all call sites — deferred to a follow-up iteration.
- **Decision**: Incremental > perfect. The csproj conversion is the structural foundation. Further splitting (MarkdownRenderer, ContentRenderer, SessionBrowser, etc.) can follow incrementally now that the project structure supports multiple files.
- All 67 tests pass after conversion.

### 2025-02-24: ColorHelper extraction complete
- **Fixed 19 build errors** from interrupted ColorHelper migration on `refactor/csproj-split` branch:
  - Added `using Spectre.Console;` to ContentRenderer.cs (3 AnsiConsole references)
  - Replaced captured `full` variable with `colors.Full` in ContentRenderer (lines 419, 529)
  - Fixed replay.cs constructor: `new MarkdownRenderer(noColor)` and `new ContentRenderer(colors, mdRenderer)`
  - Replaced all `SummarySerializerOptions` → `ColorHelper.SummarySerializer` (5 occurrences)
  - Replaced all `JsonlSerializerOptions` → `ColorHelper.JsonlSerializer` (5 occurrences)
- **Display width bug fixes** for CJK/emoji support:
  - MarkdownRenderer: Changed table column width calculations from `.Length` to `VisibleWidth()` (lines 191, 200)
  - replay.cs session browser: Changed truncation from string slicing to `TruncateToWidth()` and length checks to `VisibleWidth()` (line 2011)
- Build now succeeds with only the pre-existing CS0162 warning (unreachable code).
- **Pattern learned:** When extracting helpers that hold serializer options, all JsonSerializer.Serialize() call sites must be updated to reference the static property (e.g., `ColorHelper.SummarySerializer`).

### 2025-02-24: StatsAnalyzer extraction complete
- **Extracted StatsAnalyzer.cs**: Moved `ExtractStats` and `OutputStatsReport` functions from replay.cs into new StatsAnalyzer class.
- **Design**: Primary constructor takes `ColorHelper colors` (not used directly, but preserved for future use) and three function delegates for parsing (`parseJsonlData`, `parseClaudeData`, `parseWazaData`). These parsing functions remain as top-level local functions in replay.cs since they capture variables.
- **Pattern**: When extracting functions that depend on other local functions, pass them as delegates rather than extracting them too (incremental refactoring).
- **Updated replay.cs**: Created `statsAnalyzer` instance after `colors` initialization (~line 100), replaced all `ExtractStats` and `OutputStatsReport` call sites (3 locations total) with `statsAnalyzer.Method()`, and removed the original 350+ line function definitions.
- Build succeeds with CS9113 warning (unread parameter `colors`) — acceptable since it's captured for future use. No functionality changes.

### 2025-02-24: Session browser merge bug fixed
- **Bug**: `BrowseSessions` used `else if` on line 1715, causing filesystem sessions to be completely skipped when DB loaded successfully. Sessions existing only on filesystem (older sessions, different machines) never appeared in the browser.
- **Fix**: Changed `else if (dbPathOverride == null)` → `if (dbPathOverride == null)` so filesystem scan always runs (except with external `--db`). Added deduplication: check `knownSessionIds.Contains(id)` before adding filesystem sessions, and add filesystem IDs to `knownSessionIds` after adding to prevent Claude Code scan duplicates.
- **Locations**: Line 1715 (else if → if), line 1742 (added continue check), line 1752 (added knownSessionIds.Add).
- **Result**: All three session sources (DB, filesystem, Claude Code) now merge correctly, sorted by updatedAt descending. DB polling loop already used knownSessionIds for deduplication, so no changes needed there.

### 2025-02-24: InteractivePager extraction complete
- **Extracted InteractivePager.cs**: Moved the `RunInteractivePager<T>` method and all its nested helper functions (~615 lines) from replay.cs into a new InteractivePager class.
- **Design**: Primary constructor takes dependencies: `ColorHelper colors`, `ContentRenderer cr`, `bool noColor`, `string? filePath`, `string? filterType`, `bool expandTools`, `int? tail`. The main method is now `Run<T>()` instead of `RunInteractivePager<T>()`.
- **Dependencies**: Relies on TextUtils static functions (GetVisibleText, VisibleWidth, TruncateToWidth, etc.), SafeGetString from TextUtils, and ProcessEvalEvent from EvalProcessor. All nested local functions (RebuildContent, RebuildSearchMatches, Render, WriteMarkupLine, etc.) became nested local functions within the Run method to maintain closure over method-local state.
- **Updated replay.cs**: Created `pager` instance after `cr` initialization (~line 100), replaced all 3 `RunInteractivePager` call sites with `pager.Run()`, and removed the original 615-line method definition.
- **Result**: replay.cs reduced from ~2,027 lines to 1,410 lines. Build succeeds with CS9113 warnings (unread parameters `colors` and `tail` — preserved for future use). All 67 tests pass.
- **Pattern**: Terminal UI code with stateful loops can be extracted to classes while keeping local function closures inside the main method to avoid threading shared state through every helper.


