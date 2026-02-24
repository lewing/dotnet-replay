# Decisions

> Shared decision ledger. All agents read this before starting work. Scribe merges entries from the inbox.

<!-- Append decisions below. Format: ### {timestamp}: {topic} -->

### 2025-02-24: DB Features Implementation

**Author:** Batty  
**Status:** Completed

Two features implemented for session database management:

1. **Periodic DB Re-query in Browser Mode**: Background scan thread now polls SQLite DB every 5 seconds for new sessions. Tracks known session IDs in `HashSet<string>` to avoid duplicates. Only re-polls DB; filesystem and Claude Code scans remain one-shot.

2. **`--db <path>` CLI Argument**: Enables browsing sessions from external session-store.db files. CLI parsing treats `.db` files as dbPath. `LoadSessionsFromDb` and `BrowseSessions` accept optional `dbPathOverride` parameter. When external DB is used, filesystem fallback is skipped.

**Impact:** Live browsing shows new sessions automatically; users can browse databases from CI/CD environments or backups; no breaking changes.

### 2025-07-22: Convert replay.cs from file-based app to .csproj project

**Author:** Batty (Core Developer)  
**Status:** Implemented

The `replay.cs` file had grown to ~4282 lines as a single-file .NET 10 app. Converted from file-based app (`dotnet run replay.cs`) to standard .csproj-based project (`dotnet run --project dotnet-replay.csproj`).

**Phase 1 Completed:**
1. Created `dotnet-replay.csproj` — migrated all `#:property`/`#:package` directives to standard MSBuild properties and PackageReference items
2. Extracted `Models.cs` — moved all record types to a separate file
3. Stripped `#:` directives from replay.cs (now redundant with .csproj)
4. Updated test project — removed `<Compile Include>` and `<Features>FileBasedProgram</Features>`, changed process invocations from `dotnet run {file}` to `dotnet run --project {csproj}`

**Phase 2 (future):**
- Extract `ReplayOptions` shared config object
- Extract `MarkdownRenderer`, `ContentRenderer`, `SessionBrowser`, `StatsAnalyzer`, `EvalProcessor` as classes

**Verification:**
- `dotnet build dotnet-replay.csproj` — succeeds with 0 errors, 0 warnings
- `dotnet test tests/ReplayTests.csproj` — all 67 tests pass

### 2025-02-24T16:42:09Z: User directive

**By:** Larry Ewing (via Copilot)  
**What:** Project-based tool (.csproj) is acceptable for replay — no need to preserve file-based app format as long as nothing breaks.  
**Why:** User request — captured for team memory

### 2025-02-24: replay.cs Split Strategy Analysis

**Author:** Deckard  
**Status:** Proposed  
**Requested by:** Larry Ewing

**Context:** replay.cs has grown to ~4,542 lines. Analysis on `.NET 10 File-Based App Constraints` and recommended split strategy using `#:project` directive referencing a class library.

**Key Finding:** `#:project` directive IS supported for decomposing file-based apps. Class libraries can be referenced while keeping replay.cs as entry point.

**Recommended Structure:**
- `replay.cs` entry point (~500–700 lines, retains `#:` directives and CLI parsing)
- `lib/Replay.Lib.csproj` class library containing:
  - Models.cs (record types)
  - ReplayOptions.cs (shared options)
  - MarkdownRenderer.cs (~600 lines)
  - ContentRenderer.cs (~1200 lines)
  - SessionBrowser.cs (~700 lines)
  - StatsAnalyzer.cs (~400 lines)
  - Utilities.cs (~300 lines)

**Rationale:** Incremental approach; Models extraction is zero-risk (no behavior change); every function in replay.cs captures top-level variables, making class conversion higher-risk and better done separately.

**Status:** Proposed; awaiting decision on incremental migration order.
