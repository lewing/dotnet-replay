# Project Context

- **Owner:** Larry Ewing
- **Project:** dotnet-replay — Interactive terminal viewer for Copilot CLI sessions and waza evaluation transcripts
- **Stack:** C# / .NET 10, single-file app (replay.cs ~180KB), Spectre.Console, Markdig, Microsoft.Data.Sqlite
- **Repository:** https://github.com/lewing/dotnet-replay
- **Created:** 2026-02-24

## Test Infrastructure

- Tests live in `tests/` directory (ReplayTests.csproj)
- Test pattern: subprocess-based — tests invoke the file-based app and check output
- Test files: JsonOutputTests.cs, SummaryOutputTests.cs, StatsOutputTests.cs, EdgeCaseTests.cs, DbFallbackTests.cs
- Test data: `tests/testdata/` contains sample JSONL, JSON, and fixture files
- 35 tests passing as of v0.5.3
- Run with: `dotnet test` from the `tests/` directory

## Learnings

<!-- Append new learnings below. Each entry is something lasting about the project. -->

### 2026-03-14: Checkpoint Navigation Tests (Tier 1)

- Created `tests/CheckpointTests.cs` — 11 unit tests for `DataParsers.LoadCheckpointsForSession()`.
- Tests use in-memory SQLite (`Data Source=:memory:`) for speed and isolation — no temp files needed.
- Adapted to Batty's actual API: `LoadCheckpointsForSession(SqliteConnection, string)` takes an open connection, not a path. `CheckpointRow` has non-null `Title` (null DB values → `"Checkpoint {N}"` fallback).
- Tests cover: happy path, empty results, missing table, null fields, ordering, reverse insertion order, session isolation, empty table.
- Build blocked by Batty's WIP compile errors in XenoPager.cs/InteractivePager.cs — all 14 errors are in main project code, zero in test code. Tests will pass once Batty's implementation compiles.
- Pattern note: DB tests that call production code directly (via `DataParsers.LoadCheckpointsForSession`) are cleaner than the replicated-logic pattern used in `DbFallbackTests.cs` — prefer this when InternalsVisibleTo allows it.

### 2026-04-02: Checkpoint Navigation Tests Complete & All Passing
- All 11 CheckpointTests.cs tests now passing green
- Build clean, integrated with Batty's implementation
- Pattern adopted project-wide: in-memory SQLite + direct API calls for all future DB-related tests
- Tier 1 checkpoint navigation feature is fully tested and verified
