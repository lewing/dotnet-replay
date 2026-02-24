# Decision: Convert replay.cs from file-based app to .csproj project

**Author:** Batty (Core Developer)
**Date:** 2025-07-22
**Status:** Implemented

## Context

The `replay.cs` file had grown to ~4282 lines as a single-file .NET 10 app using `#:property` and `#:package` directives. This made it difficult to split into multiple files, add proper IDE support, and evolve the architecture incrementally.

## Decision

Convert from a file-based app (`dotnet run replay.cs`) to a standard .csproj-based project (`dotnet run --project dotnet-replay.csproj`) in two phases:

### Phase 1 (this change):
1. **Create `dotnet-replay.csproj`** — migrate all `#:property`/`#:package` directives to standard MSBuild properties and PackageReference items
2. **Extract `Models.cs`** — move all record types (no state dependencies) to a separate file
3. **Strip `#:` directives** from replay.cs (now redundant with .csproj)
4. **Update test project** — remove `<Compile Include>` and `<Features>FileBasedProgram</Features>`, change process invocations from `dotnet run {file}` to `dotnet run --project {csproj}`

### Phase 2 (future):
- Extract `ReplayOptions` shared config object to replace captured top-level variables
- Extract `MarkdownRenderer`, `ContentRenderer`, `SessionBrowser`, `StatsAnalyzer`, `EvalProcessor` as classes taking `ReplayOptions`
- Leave deeply tangled code (RunInteractivePager) for last

## Rationale

- **Incremental > perfect**: The .csproj is the structural prerequisite for all future splitting. Getting it right first, with all tests passing, creates a stable foundation.
- **Local function capture is the hard part**: Every function in replay.cs captures top-level variables. Converting to class methods requires threading a shared options object through hundreds of call sites — a separate, higher-risk change.
- **Models extraction is safe**: Record types have no captured state, making them the ideal first extraction target.

## Verification

- `dotnet build dotnet-replay.csproj` — succeeds with 0 errors, 0 warnings
- `dotnet build tests/ReplayTests.csproj` — succeeds
- `dotnet test tests/ReplayTests.csproj` — all 67 tests pass
