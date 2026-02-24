# Decision: Stats Analysis Extraction Pattern

**Date:** 2025-02-24  
**Author:** Batty (Core Developer)  
**Status:** Implemented

## Context

The `replay.cs` file (~2983 lines) is being refactored to split monolithic code into separate classes. Previously extracted: TextUtils.cs, MarkdownRenderer.cs, EvalProcessor.cs, ContentRenderer.cs, Models.cs, ColorHelper.cs.

The stats command functionality (`ExtractStats` and `OutputStatsReport`, ~420 lines) needed extraction.

## Decision

Extracted stats analysis into `StatsAnalyzer.cs` with the following design:

```csharp
class StatsAnalyzer(
    ColorHelper colors,
    Func<string, JsonlData?> parseJsonlData,
    Func<string, JsonlData?> parseClaudeData,
    Func<JsonDocument, WazaData> parseWazaData)
{
    public FileStats? ExtractStats(string filePath) { ... }
    public void OutputStatsReport(List<FileStats> stats, string? groupBy, bool asJson, int? failThreshold) { ... }
}
```

## Key Patterns

1. **Delegate Injection for Local Functions**: Rather than extracting `ParseJsonlData`, `ParseClaudeData`, and `ParseWazaData` (which capture many top-level variables), we pass them as function delegates. This enables incremental refactoring without requiring a massive shared context object.

2. **Static Serializer Access**: `ColorHelper.SummarySerializer` and `ColorHelper.JsonlSerializer` are accessed as static properties, not through the instance.

3. **Constructor Parameter Capture**: The `colors` parameter is captured by the primary constructor but not directly used in methods. This generates a CS9113 warning but is acceptable — keeps the API surface clean for future expansion.

## Alternatives Considered

- **Extract parsing functions too**: Would require threading a large context object through all parsing functions or making them static methods in a helper class. Deferred for incremental refactoring.

- **Remove unused `colors` parameter**: Would break consistency with other extracted classes (ContentRenderer, MarkdownRenderer) which all take ColorHelper. Better to keep the API uniform.

## Result

- `replay.cs` reduced from 2983 lines to ~2560 lines
- Stats analysis logic (~420 lines) now isolated and testable
- Build succeeds with 2 warnings (CS9113 unread parameter, CS0162 unreachable code)
- All existing call sites updated (3 locations in stats command dispatch)
- No functionality changes

## Impact

Positive:
- Stats logic isolated for easier testing and maintenance
- Follows pattern established by other extractions
- Incremental refactoring strategy proven effective

Neutral:
- Delegate pattern adds slight complexity to initialization
- CS9113 warning acceptable but visible

## Follow-up Work

Consider extracting parsing functions to a `FormatParser` or `TranscriptParser` static class once the scope of required context is clearer. For now, function delegates keep the interface clean.
