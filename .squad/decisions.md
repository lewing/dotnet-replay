# Decisions

> Shared decision ledger. All agents read this before starting work. Scribe merges entries from the inbox.

<!-- Append decisions below. Format: ### {timestamp}: {topic} -->

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
