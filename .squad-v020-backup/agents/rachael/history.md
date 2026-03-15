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
