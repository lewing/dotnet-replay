# Session Browser Merge Bug Fixed

**Date**: 2025-02-24  
**Decided by**: Batty  
**Status**: Implemented on `refactor/csproj-split`

## Context

The session browser (`BrowseSessions` function in replay.cs) loads sessions from three sources:
1. SQLite DB (session-store.db)
2. Filesystem (workspace.yaml + events.jsonl directories)
3. Claude Code (~/.claude/projects/)

Line 1715 used `else if`, causing filesystem sessions to be skipped entirely when the DB loaded successfully.

## Problem

Sessions that existed only on the filesystem (e.g., older sessions predating the DB, or sessions from a different machine) never appeared when the DB loaded. Users lost visibility into their complete session history.

## Decision

Changed the control flow from:
```csharp
if (dbSessions != null) { ... }
else if (dbPathOverride == null) { /* filesystem */ }
```

To:
```csharp
if (dbSessions != null) { ... }
if (dbPathOverride == null) { /* filesystem */ }
```

And added deduplication:
- Check `knownSessionIds.Contains(id)` before adding filesystem sessions
- Add filesystem session IDs to `knownSessionIds` after adding to allSessions
- Claude Code scan already checked knownSessionIds, so it automatically deduplicates
- DB polling loop already used knownSessionIds, so no changes needed there

## Outcome

All three session sources now merge correctly. Sessions are deduplicated by ID and sorted by `updatedAt` descending. Build succeeds with 0 errors.

## Alternatives Considered

None — this was the surgical minimal fix. The `knownSessionIds` HashSet infrastructure was already present for DB polling.
