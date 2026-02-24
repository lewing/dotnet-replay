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

### 2025-02-24: DB Path Feature Tests (DbPathTests.cs)

Added comprehensive tests for the new `--db <path>` CLI feature in tests/DbPathTests.cs. Key testing patterns:

- **Subprocess invocation**: Tests use `dotnet run replay.cs -- <args>` to invoke the app as a subprocess
- **TTY detection**: Must redirect StandardInput and close it immediately to trigger Console.IsOutputRedirected detection
- **Dual output capture**: Return tuple `(stdout, stderr)` to verify error messages appear on stderr
- **Test data cleanup**: Create temporary .db files and clean them up with try/finally
- **Error behavior**: The app correctly detects redirected output and shows "Error: Cannot use --db in redirected output" on stderr

All 5 tests pass successfully covering: nonexistent file handling, redirected output detection, positional .db argument sugar, missing argument error, and help text inclusion.
