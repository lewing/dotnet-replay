using System.Text;

// ========== Parsed data structures ==========
record JsonlData(
    List<System.Text.Json.JsonDocument> Events,
    List<(string type, System.Text.Json.JsonElement root, DateTimeOffset? ts)> Turns,
    string SessionId, string Branch, string CopilotVersion, string Cwd,
    DateTimeOffset? StartTime, DateTimeOffset? EndTime, int EventCount)
{
    public int EventCount { get; set; } = EventCount;
}

record WazaData(
    System.Text.Json.JsonElement[] TranscriptItems,
    string TaskName, string TaskId, string Status, string Prompt, string FinalOutput,
    double DurationMs, int TotalTurns, int ToolCallCount, int TokensIn, int TokensOut,
    List<(string name, double score, bool passed, string feedback)> Validations,
    string ModelId = "", double AggregateScore = 0, string[] ToolsUsed = null!);

class EvalCaseResult
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool? Passed { get; set; }
    public double DurationMs { get; set; }
    public int ToolCallCount { get; set; }
    public List<string> ToolsUsed { get; set; } = [];
    public int ResponseLength { get; set; }
    public string? AssertionFeedback { get; set; }
    public string? Error { get; set; }
    public StringBuilder MessageAccumulator { get; } = new();
    public List<(string tool, string id, double durationMs)> ToolEvents { get; set; } = [];
    public Dictionary<string, DateTimeOffset> PendingTools { get; } = new();
}

class EvalData
{
    public string Suite { get; set; } = "";
    public string Description { get; set; } = "";
    public int CaseCount { get; set; }
    public List<EvalCaseResult> Cases { get; set; } = [];
    public int TotalPassed { get; set; }
    public int TotalFailed { get; set; }
    public int TotalSkipped { get; set; }
    public double TotalDurationMs { get; set; }
    public int TotalToolCalls { get; set; }
    public string? CurrentCase { get; set; }
}

enum PagerAction { Quit, Browse, Resume }

// ========== JSON output records ==========
record TurnOutput(
    int turn,
    string role,
    string? timestamp = null,
    string? content = null,
    int? content_length = null,
    string[]? tool_calls = null,
    string? tool_name = null,
    string? status = null,
    string? args = null,
    string? result_status = null,
    int? result_length = null,
    string? result = null);

record TurnCounts(int user, int assistant, int tool_calls);
record TokenCounts(int input, int output, int total);
record ValidationOutput(string name, double score, bool passed, string feedback);

record SessionSummary(
    string session_id,
    int duration_seconds,
    string duration_formatted,
    string? start_time,
    string? end_time,
    TurnCounts turns,
    string[] skills_invoked,
    Dictionary<string, int> tools_used,
    string agent,
    int errors,
    string? last_activity);

record WazaSummary(
    string task_name,
    string task_id,
    string status,
    double duration_ms,
    string duration_formatted,
    int total_turns,
    int tool_calls,
    TokenCounts tokens,
    string model,
    double aggregate_score,
    ValidationOutput[] validations);

record FileStats(
    string FilePath,
    string Format,
    string? Model,
    string? TaskName,
    string? TaskId,
    string? Status,
    int TurnCount,
    int ToolCallCount,
    int ErrorCount,
    double DurationSeconds,
    double? AggregateScore,
    bool? Passed,
    Dictionary<string, int> ToolUsage,
    string? Agent);
