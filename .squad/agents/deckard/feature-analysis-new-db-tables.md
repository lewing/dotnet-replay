# Feature Recommendations: Copilot CLI Session-Store Enhancements

## Executive Summary

The new `turns`, `checkpoints`, `session_files`, `session_refs`, and `search_index` tables represent a **fundamental upgrade** to session introspection. Currently, dotnet-replay treats sessions as black-box transcripts; these tables enable **cross-session search, granular navigation, dependency tracking, and workflow reconstruction**.

Three recommendations emerge from architecture analysis:

1. **Checkpoint Navigation (High Value, Low Lift)** — Jump to checkpoints marked during a session
2. **Full-Text Search Across Sessions (Transformational, Medium Lift)** — Find patterns, techniques, and commands across your session history  
3. **Session Dependency Graph (Strategic, High Lift)** — Visualize which sessions reference which files/PRs/issues

---

## Analysis: Current Architecture vs. New Capabilities

### What the Code Does Today

- **SessionBrowser.cs**: Loads session metadata from `sessions` table → creates `BrowserSession` grid  
- **InteractivePager.cs**: Single-session search with `/` (case-insensitive substring match)
- **DataParsers.cs**: Converts JSONL events → normalized `JsonlData` structure  
- **ContentRenderer.cs**: Renders turns as formatted text blocks
- **StatsAnalyzer.cs**: Batch aggregates (tool usage, pass/fail counts) across files

**Blind spots:**
- No knowledge of individual turns (stored in `turns` table)
- No access to checkpoints users created mid-session
- No file provenance (which files were edited/viewed in which sessions?)
- No semantic tracking (what GitHub issues/PRs were referenced?)
- No full-text search index (search is per-session, substring only)

### What the New Tables Enable

```
sessions ──┬─→ turns (turn_index, user_message, assistant_response)
           ├─→ checkpoints (title, overview, work_done, next_steps)
           ├─→ session_files (file_path, tool_name: edit/create/view)
           ├─→ session_refs (ref_type: commit/pr/issue, ref_value)
           └─→ search_index (FTS5 full-text across all turn content)
```

**Key insight:** These tables shift the session-store from "session list" to "semantic knowledge graph."

---

## Prioritized Feature Recommendations

### 🥇 TIER 1: Checkpoint Navigation (Value: ★★★★☆ | Effort: ★★☆☆☆)

**What:** Jump to named checkpoints within a session pager.

**Why:** Developers regularly save checkpoints mid-session ("After auth fix", "Before perf tuning"). Checkpoints are already captured in the DB but invisible to the UI. This is **low friction / high signal**.

**User Experience:**
```
While viewing a session:
  - New keybinding: `c` → list checkpoints in session
  - Preview shows checkpoint title, turn_index, work summary
  - Hitting Enter jumps pager to that turn
  
Example:
  Checkpoints in session abc-123:
    [1] "Initial auth implementation" (turn 3)
    [2] "Refactor after test failure" (turn 12)  
    [3] "Performance fixes" (turn 18)
  → Jump to [2] → pager scrolls to turn 12
```

**Implementation:**
- **DataParsers.cs**: Add method `LoadCheckpointsForSession(sessionId)` → queries `checkpoints` table
- **Models.cs**: Add `CheckpointRow` record with turn_index, title, work_done summary
- **InteractivePager.cs**: Register `c` command → render checkpoint menu → calculate scroll offset to checkpoint turn
- **ContentRenderer.cs**: Format checkpoint summary inline

**Files changed:** DataParsers.cs (+20 LOC), Models.cs (+3 LOC), InteractivePager.cs (+40 LOC), ContentRenderer.cs (+15 LOC) = ~80 LOC total

**Iteration cost:** LOW. Incremental on existing models + keybinding pattern already proven.

---

### 🥈 TIER 2: Full-Text Search Across Sessions (Value: ★★★★★ | Effort: ★★★☆☆)

**What:** Query `search_index` FTS5 table to find turns matching a pattern across *all* sessions.

**Why:** Users ask **"How did I handle OAuth last time?"** or **"Show me all failures involving Roslyn"**. Currently, they manually browse sessions → search within each. This feature **bridges the replay tool's biggest UX gap**.

**User Experience:**
```
$ replay --search "socket timeout" --json
[{"session": "abc-123", "turn": 5, "role": "assistant", "content": "...socket timeout..."}]
[{"session": "def-456", "turn": 12, "role": "user", "content": "...socket timeout..."}]

Or interactive:
$ replay
  → Browse sessions (existing) → NEW: `S` → "Full-text search across sessions"
  → Enter query: "oauth redirect"
  → Results pane shows matching turns across sessions
  → Hit Enter → jump to selected session + turn
```

**Implementation:**
- **CLI parsing (replay.cs)**: New `--search <query>` flag
- **SessionBrowser.cs / XenoSessionBrowser.cs**: Add full-text query method using `search_index MATCH 'query'`
- **Models.cs**: Add `CrossSessionSearchResult` record (session_id, turn_index, turn_role, snippet)
- **ContentRenderer.cs**: Format search results as compact 1-line summary per match
- **InteractivePager.cs**: NEW "search results" view that lists matching turns, allows navigation

**Files changed:** replay.cs (+30 LOC for CLI), SessionBrowser.cs (+40 LOC), Models.cs (+5 LOC), ContentRenderer.cs (+20 LOC), InteractivePager.cs (+50 LOC) = ~145 LOC total

**Tech notes:**
- FTS5 `MATCH` query syntax: `search_index MATCH 'oauth OR redirect'` (already indexed by Copilot CLI)
- Results paginated (FTS5 supports LIMIT/OFFSET)
- Snippet context: retrieve surrounding turns for context

**Strategic value:** This single feature **multiplies replay's utility** by making knowledge extractable from archives.

---

### 🥉 TIER 3: Session Dependency Graph (Value: ★★★★☆ | Effort: ★★★★☆)

**What:** Visualize which sessions reference which files, commits, or issues via `session_files` + `session_refs`.

**Why:** Power users want to trace workflows: **"Which sessions touched the auth module? Did any of them reference issue #123?"** This is a **domain-specific query** that's hard without structured data.

**User Experience:**
```
$ replay --refs
  → Session Browser shows columns: Files Modified | Refs (PRs/Issues)
  → Hover over file list → see which tools (edit/create/view) touched them
  → Click issue reference → browse all sessions referencing that issue

Visual example (simplified grid):
  Session         | Files              | Refs
  abc-123         | auth.ts, index.ts  | PR #42, issue #123
  def-456         | config.ts          | issue #121
  ghi-789         | auth.ts            | PR #44, issue #125
```

**Implementation:**
- **SessionBrowser.cs / XenoSessionBrowser.cs**: New `LoadSessionDependencies(sessionId)` → queries `session_files` + `session_refs`
- **Models.cs**: Add `SessionDependencies` record with file list + ref list + tool annotations
- **InteractivePager.cs**: New info panel showing files touched in current session + refs encountered
- **XenoSessionBrowser.cs**: Extend DataGrid with sortable "Files" / "Refs" columns (if layout permits)

**Files changed:** SessionBrowser.cs (+60 LOC), XenoSessionBrowser.cs (+40 LOC), Models.cs (+8 LOC), InteractivePager.cs (+30 LOC) = ~138 LOC

**Tech notes:**
- `session_files` joins to session via session_id; group by file_path + aggregate tools
- `session_refs` maps to {commit, pr, issue}; deduplicate ref_value
- For XenoSessionBrowser: New columns may require horizontal scrolling (already implemented in Phase 2)
- Filter by file or ref type: `--filter-files "auth*"` or `--filter-refs "PR"`

**When to prioritize:** AFTER checkpoint navigation + search prove valuable; requires XenoSessionBrowser UI expansion.

---

## Alternative / Follow-on Features (Lower Priority)

### 4. Checkpoint Bookmarking
- Add `--bookmark <checkpoint-title>` flag to auto-navigate pager to checkpoint on load
- E.g., `replay session.jsonl --bookmark "Test fix"`
- **Effort:** ★★☆☆☆ | **Value:** ★★☆☆☆ (convenience feature; covers 10% of use case)

### 5. Session Diff Mode
- Query `session_files` to show what files changed between two sessions
- Helps identify "what did I change in session B that wasn't in session A?"
- Aligns with existing GitHub issue #11
- **Effort:** ★★★☆☆ | **Value:** ★★★☆☆ (tactical; useful for code archaeology)

### 6. Turn-Level Export
- Export individual turns to Markdown/JSON with full context
- Uses `turns` table directly instead of JSONL re-parsing
- **Effort:** ★★☆☆☆ | **Value:** ★★☆☆☆ (niche; maybe blog-post use case)

---

## Implementation Roadmap

### Phase 1: Foundation (Recommend **STARTING HERE**)
1. **Checkpoint Navigation** — High confidence, low risk, establishes DB table usage patterns
2. Batty implements; Rachael adds tests

### Phase 2: Search
1. **Full-Text Search** — Proven FTS5 index exists; unlock discoverability
2. Batty implements search CLI + SessionBrowser query; Rachael tests cross-session results

### Phase 3: Visualization (Optional; depends on adoption)
1. **Dependency Graph** — Requires XenoSessionBrowser UI expansion
2. Batty extends DataGrid; Deckard reviews for UX coherence

---

## Architectural Notes for Implementation

### Existing Patterns to Preserve
- **Parameter injection:** DataParsers, ContentRenderer injected into SessionBrowser/Pager
- **Thread-safe DB reads:** Both browsers use `SqliteConnection` read-only mode + locks for in-flight scans
- **Graceful degradation:** If `turns`/`checkpoints` tables missing, features silently disabled (no hard failures)

### New DB Interaction Points
- **DataParsers.cs** should own turn/checkpoint loading (similar to JSONL parsing)
- **SessionBrowser.cs** should own cross-session queries (search_index joins)
- **Models.cs** records for domain entities (Checkpoint, SearchResult, SessionDependency)

### Testing Strategy
- **Unit tests** for new models + query builders (mock SQLite results)
- **Integration tests** for full-text search (use real search_index if available)
- **E2E tests** for pager keybindings (existing test infrastructure)

---

## Why These Three Emerge as Top Priorities

| Feature | User Pain Point | DB Leverage | Scope | Iteration Speed |
|---------|-----------------|-------------|-------|-----------------|
| **Checkpoints** | "Lost where I saved that fix" | `checkpoints.turn_index` | Single-session | Days (known data model) |
| **Search** | "Did I solve this before?" | `search_index` FTS5 | Multi-session | 1-2 weeks (new view) |
| **Dependencies** | "What touched auth?" | `session_files`, `session_refs` | Analytics | 2-3 weeks (UI expansion) |

---

## Risk Assessment

### Low Risk
- ✅ **Checkpoint navigation:** DB table already normalized; keybinding pattern proven in XenoPager
- ✅ **Search:** FTS5 index already built by Copilot CLI; no writes needed

### Medium Risk
- ⚠️ **Dependency graph:** Requires UI layout changes (new columns); may clash with horizontal scrolling feature (Phase 2)

### Mitigation
- Start with Tier 1 (checkpoint) as proof-of-concept
- Add comprehensive integration tests before Tier 2 (search) ships
- Defer Tier 3 (dependencies) until XenoSessionBrowser layout stabilizes

