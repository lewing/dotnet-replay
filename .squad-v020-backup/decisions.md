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
