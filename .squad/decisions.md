# Decisions

> Shared decision ledger. All agents read this before starting work. Scribe merges entries from the inbox.

<!-- Append decisions below. Format: ### {timestamp}: {topic} -->

### 2026-04-02: Checkpoint Navigation Implementation Complete (Tier 1)

**Author:** Batty, Rachael, Deckard  
**Status:** Completed

Tier 1 of the database feature roadmap (Checkpoint Navigation) is now complete and tested. Feature allows navigation to named checkpoints within a session via `c` keybinding in both Spectre and XenoAtom pagers.

**Deliverables:**
- `CheckpointRow` record in Models.cs
- `LoadCheckpointsForSession` + `ResolveSessionDbContext` in DataParsers.cs
- `c` keybinding + checkpoint UI in InteractivePager.cs and XenoPager.cs
- 11 unit tests in CheckpointTests.cs (all passing)
- Build clean, 84 tests passing

**Unblocks:** Tier 2 (Full-Text Search) after adoption feedback

---

### 2026-03-16: In-Memory SQLite Testing Pattern for DB Tests

**Author:** Rachael  
**Status:** Completed

Established testing pattern for database-related features: use in-memory SQLite connections and call production methods directly instead of replicating DB logic in tests.

**Pattern:**
- Test infrastructure: `SqliteConnection(":memory:")` for isolation and speed
- API calls: Direct to `DataParsers` methods via InternalsVisibleTo
- Advantage: Single source of truth; test failures catch real regressions

**Applied in:** CheckpointTests.cs (11 tests covering happy path, edge cases, graceful degradation)

---

### 2026-03-16: 3-Tier Feature Roadmap: New Database Tables

**Author:** Deckard (Lead)  
**Status:** Approved

**Tier 1 — Checkpoint Navigation** ✅ COMPLETED
- Keybinding `c` → checkpoint list → detail view
- Implementation: ~80 LOC; 1–2 days; LOW RISK
- Value: ★★★★☆ | Effort: ★★☆☆☆

**Tier 2 — Full-Text Search** (Planned)
- CLI flag `--search <query>` + interactive results pane
- Implementation: ~145 LOC; 1–2 weeks; MEDIUM RISK
- Value: ★★★★★ | Effort: ★★★☆☆
- Addresses issue #12 (grep across transcripts)

**Tier 3 — Session Dependency Graph** (Deferred)
- Files touched + GitHub refs per session
- Timeline: 2–3 weeks after Tier 2
- Precondition: Phase 2 (XenoSessionBrowser horizontal scroll) stable
- Value: ★★★★☆ | Effort: ★★★★☆

**Architecture:** Maintain parameter injection (DataParsers, ContentRenderer); graceful degradation if tables missing; comprehensive testing before each tier ships.

---

### 2026-03-16: PR #149 Review Fixes (dotnet/skills)

**Author:** Batty  
**Status:** Completed

Resolved seven review comments from Copilot on `dotnet/skills` incremental-results branch:

**Decisions:**
1. Persist judge model in `sessions.db` via `schema_info["judge_model"]` when `validate --keep-sessions` creates DB
2. Make `rejudge` require `--judge-model` flag for older DBs without persisted metadata
3. Use fresh temporary directories for rejudge judge/pairwise sessions (not saved run WorkDir)
4. Cache skill directory SHA once per evaluation; pass into each run instead of recomputing

**Why:** `sessions.model` tracks agent model, not judge model. Persisted work dirs are ephemeral. Length-prefixed hashing prevents directory SHA collisions.

---

### 2026-03-16: XenoSessionBrowser Preview Pane Collapse

**Author:** Batty  
**Status:** Completed

Fixed XenoAtom session browser preview pane collapse to restore session list to full width when preview is hidden.

**Decisions:**
1. Treat preview toggle as layout change, not just visibility toggle
2. Collapse preview by setting `previewBorder.MaxWidth = 0` when hidden; restore fixed width when shown
3. Cancel in-flight preview work and clear pending text when hiding pane

**Why:** `IsVisible = false` was insufficient in HStack layout; width constraint must reset. Clearing stale work prevents delayed output from repopulating collapsed pane.

---

### 2026-03-16: Rubric Persistence and Judge-Mode Validation

**Author:** Batty  
**Status:** Completed

Resolved two rejudge issues: missing scenario rubrics and silent fallback on mistyped `--judge-mode` values.

**Decisions:**
1. Persist scenario rubric snapshot in `sessions.db` as `sessions.rubric` JSON column
2. Serialize missing rubrics as `[]` (not NULL) so NULL signals legacy-only data
3. Self-migrate older `sessions.db` in `SessionDatabase.Initialize()` with rubric column + version bump
4. Validate `--judge-mode` with `AcceptOnlyFromAmong()` to fail fast on typos

**Why:** Rejudge must score against original rubric. Differentiating `[]` from NULL keeps warnings precise. Self-migration preserves older result directory compatibility. Strict option validation prevents operator mistakes.

---

### 2026-03-16: SDK Hook Analysis — Session Forking Feasibility

**Author:** Batty  
**Status:** Completed (Research)

Analyzed GitHub.Copilot.SDK v0.1.32 hook API to determine if tool result mocking is feasible for session forking.

**Finding:** Yes, tool result substitution is possible today via multiple mechanisms:
- `PostToolUseHookOutput.ModifiedResult` — Replace tool result before LLM sees it
- `SessionConfig.Tools` — Register custom tools shadowing built-ins
- `ResumeSessionConfig` — Fork from checkpoint with replay hooks installed

**Recommendation:** 
- Upgrade Tempest from 0.1.25 → 0.1.32 (same hook shape; adds ResumeSessionConfig + workspace APIs)
- No SDK changes needed for prototype
- Implement hybrid approach: read-only tools use ModifiedResult; write tools use shadow custom tools

**Roadmap:** Recording (Phase 1) → Replay/Fork (Phase 2) → Session Forking (Phase 3)

---

### 2026-03-14: Session Browser Branch Metadata Fallback

**Author:** Batty  
**Status:** Completed

Fixed branch display by enriching sessions from per-session metadata files instead of trusting the top-level session-store DB alone.

**Decision:**
- For Copilot sessions, read `branch` and `repository` from `workspace.yaml` when available
- If either field is still missing, inspect the `session.start` event in `events.jsonl` and recover them from `data.context`
- For Claude sessions, recover branch from `gitBranch` in the JSONL stream
- Apply the same enrichment in both the Spectre browser and the XenoAtom browser, including DB-loaded sessions whose DB row has missing branch data

**Why:** Filesystem-loaded sessions were hardcoded to empty branch/repository. Real session-store DBs contain a mix of populated and empty branch values, so DB reads alone are not reliable. The per-session files are the best local fallback and keep both browsers consistent.

---

### 2026-03-14: XenoPager Horizontal Offset Tracking

**Author:** Batty  
**Status:** Completed

To support horizontal scrolling in `XenoPager.cs`, the pager mirrors the existing vertical-scroll workaround: keep a local tracked horizontal offset and only write to the reflected `ScrollViewer.HorizontalOffset` property.

**Rationale:** XenoAtom.Terminal.UI binding tracking can throw when the same offset property is read and written during one tracking context. Matching the `trackedOffset` pattern avoids that conflict and keeps horizontal scroll behavior stable across redraws.

**Implementation:**
- Added `trackedHorizontalOffset` plus `GetHorizontalOffset()` / `SetHorizontalOffset()` helpers in `XenoPager.cs`
- Bound `h`, `l`, and `0` to left/right/reset column movement
- Reapply the tracked horizontal offset after `PopulateLog()` rebuilds content

---

### 2026-03-14: XenoAtom Review Follow-up Decisions

**Author:** Batty  
**Status:** Completed

Two issues resolved:

1. **Follow Mode Memory Leak Fix** — Retain only cloned `JsonElement` turn payloads needed for rendering and update `EventCount` separately; do not append live `JsonDocument` instances to `JsonlData.Events` during tailing. Keeps long-running follow sessions from accumulating undisposed documents.

2. **Preview Selection Generation Check** — For session browser previews, the selection generation check needs to happen inside the worker before expensive parse/render work, not just when publishing the finished preview text. Reduces wasted background work when users scroll rapidly through large sessions.

---

### 2026-03-13: XenoAtom UI Feature Gaps Assessment

**Author:** Deckard (Lead)  
**Status:** Completed

Comprehensive assessment of XenoAtom.Terminal.UI against Spectre.Console baseline. Implementation is architecturally sound (0 warnings) but has 6 feature gaps: 3 critical parity blockers (navigation keys, search cycling, preview rendering) and 3 polish/regression items (horizontal scrolling, status bar info, line numbers).

**Recommendation:** Merge as secondary UI; complete Phase 1 parity items (4–7 hours) before next release.

**Key Findings:**
- XenoPager command registration in place but actual keybindings missing (Page Up/Down, Home, End, j/k/g/G)
- XenoPager search hooks LogControl.OpenSearch() but no match cycling (n/N)
- XenoSessionBrowser has DataParsers injected but unused; should leverage for preview rendering
- LogControl public API lacks generic scroll support (investigation needed)

**Open Questions for Implementation:**
1. Does LogControl expose programmatic scroll control?
2. Can we extend LogControl.OpenSearch() or must reimplement match cycling?
3. Is LogControl aware of line width for horizontal scroll?
4. Will parsing + rendering 50+ JSONL turns impact performance?

---

### 2026-03-13: XenoAtom Phase 1 Implementation Complete

**Author:** Batty  
**Status:** Completed

All 3 Phase 1 items implemented with passing tests and 0 build warnings (+225 LOC).

#### Deliverables:

1. **Pager Navigation Keys** — Implemented Page Up/Down, Home, End, vim-style j/k/g/G
   - Tech Debt: Uses reflection bridge to LogControl's internal `_scrollViewer` (set to replace if LogControl API matures)
   - Reason: LogControl v1.3.0 exposes VerticalOffset/ViewportHeight but no public scroll methods

2. **Search Match Cycling** — Implemented n/N for next/previous match navigation
   - Uses public GoToNextMatch/GoToPreviousMatch APIs (clean, no reflection)
   - Status bar shows "Match X/Y" when in search mode

3. **Browser Preview Rendering** — Replaced raw JSONL with parsed & rendered content
   - Calls DataParsers.ParseJsonlData() + ContentRenderer.RenderJsonlContentLines()
   - Limited to 50 turns for readability; graceful error handling

**Impact:** Restores feature parity for critical navigation and search workflows. Preview feature regression in browser is now resolved.

**Next Steps:** Phase 2 (horizontal scrolling, status bar), Phase 3 (regression tests), long-term: monitor LogControl API for scroll method exposure.

---

### 2025-02-24: DB Features Implementation

**Author:** Batty  
**Status:** Completed

Two features implemented for session database management:

1. **Periodic DB Re-query in Browser Mode**: Background scan thread now polls SQLite DB every 5 seconds for new sessions. Tracks known session IDs in `HashSet<string>` to avoid duplicates. Only re-polls DB; filesystem and Claude Code scans remain one-shot.

2. **`--db <path>` CLI Argument**: Enables browsing sessions from external session-store.db files. CLI parsing treats `.db` files as dbPath. `LoadSessionsFromDb` and `BrowseSessions` accept optional `dbPathOverride` parameter. When external DB is used, filesystem fallback is skipped.

**Impact:** Live browsing shows new sessions automatically; users can browse databases from CI/CD environments or backups; no breaking changes.
