using System.Text.Json;
using static TextUtils;
using static EvalProcessor;

class StatsAnalyzer(ColorHelper colors, Func<string, JsonlData?> parseJsonlData, Func<string, JsonlData?> parseClaudeData, Func<JsonDocument, WazaData> parseWazaData)
{
    public FileStats? ExtractStats(string filePath)
    {
        try
        {
            // Detect format
            var firstLine = "";
            using (var reader = new StreamReader(filePath))
            {
                firstLine = reader.ReadLine() ?? "";
            }
            
            bool isJsonl = filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                || (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && firstLine.TrimStart().StartsWith("{") && !firstLine.TrimStart().StartsWith("["));
            
            bool isClaude = false;
            if (isJsonl && IsClaudeFormat(filePath))
                isClaude = true;

            // Try Eval JSONL format
            if (isJsonl && !isClaude && IsEvalFormat(filePath))
            {
                var evalData = ParseEvalData(filePath);
                if (evalData is not null)
                {
                    var toolUsage = new Dictionary<string, int>();
                    foreach (var c in evalData.Cases)
                        foreach (var t in c.ToolsUsed)
                            toolUsage[t] = toolUsage.GetValueOrDefault(t) + 1;

                    return new FileStats(
                        FilePath: filePath,
                        Format: "eval",
                        Model: null,
                        TaskName: evalData.Suite,
                        TaskId: null,
                        Status: evalData.TotalFailed == 0 ? "passed" : "failed",
                        TurnCount: evalData.Cases.Count,
                        ToolCallCount: evalData.TotalToolCalls,
                        ErrorCount: evalData.Cases.Count(c => c.Error is not null),
                        DurationSeconds: evalData.TotalDurationMs / 1000.0,
                        AggregateScore: evalData.Cases.Count > 0 ? (double)evalData.TotalPassed / evalData.Cases.Count : 0,
                        Passed: evalData.TotalFailed == 0 && evalData.TotalPassed > 0,
                        ToolUsage: toolUsage,
                        Agent: null
                    );
                }
            }

            // Try Waza format first (JSON)
            if (!isJsonl)
            {
                try
                {
                    var jsonText = File.ReadAllText(filePath);
                    var wazaDoc = JsonDocument.Parse(jsonText);
                    var root = wazaDoc.RootElement;
                    
                    if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("transcript", out _) || root.TryGetProperty("tasks", out _))
                        || root.ValueKind == JsonValueKind.Array)
                    {
                        var wazaData = parseWazaData(wazaDoc);
                        
                        bool? passed = null;
                        if (wazaData.Validations.Count > 0)
                            passed = wazaData.Validations.All(v => v.passed);
                        
                        return new FileStats(
                            FilePath: filePath,
                            Format: "waza",
                            Model: wazaData.ModelId,
                            TaskName: wazaData.TaskName,
                            TaskId: wazaData.TaskId,
                            Status: wazaData.Status,
                            TurnCount: wazaData.TotalTurns,
                            ToolCallCount: wazaData.ToolCallCount,
                            ErrorCount: 0, // Waza doesn't track errors separately
                            DurationSeconds: wazaData.DurationMs / 1000.0,
                            AggregateScore: wazaData.AggregateScore,
                            Passed: passed,
                            ToolUsage: wazaData.ToolsUsed?.ToDictionary(t => t, _ => 1) ?? new Dictionary<string, int>(),
                            Agent: null
                        );
                    }
                }
                catch
                {
                    // Not Waza format, continue
                }
            }
            
            // Parse as JSONL (Copilot/Claude)
            if (isJsonl)
            {
                var jsonlData = isClaude ? parseClaudeData(filePath) : parseJsonlData(filePath);
                if (jsonlData == null)
                    return null;
                
                // Extract stats using OutputSummary logic
                int userMsgCount = 0, assistantMsgCount = 0, toolCallCount = 0, errorCount = 0;
                var toolUsage = new Dictionary<string, int>();
                string agentName = "";
                
                foreach (var (type, root, ts) in jsonlData.Turns)
                {
                    var data = root.TryGetProperty("data", out var d2) ? d2 : default;
                    
                    switch (type)
                    {
                        case "user.message":
                            userMsgCount++;
                            break;
                        case "assistant.message":
                            assistantMsgCount++;
                            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("toolRequests", out var toolReqs)
                                && toolReqs.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tr in toolReqs.EnumerateArray())
                                {
                                    toolCallCount++;
                                    if (tr.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                    {
                                        var toolName = nameEl.GetString() ?? "";
                                        toolUsage[toolName] = toolUsage.GetValueOrDefault(toolName) + 1;
                                    }
                                }
                            }
                            break;
                        case "tool.result":
                            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var res))
                            {
                                var status = SafeGetString(res, "status");
                                if (status == "error") errorCount++;
                            }
                            break;
                    }
                }
                
                // Extract agent name
                foreach (var ev in jsonlData.Events)
                {
                    var root = ev.RootElement;
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "session.start")
                    {
                        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("context", out var ctx))
                        {
                            agentName = SafeGetString(ctx, "agentName");
                            break;
                        }
                    }
                }
                
                var duration = jsonlData.EndTime.HasValue && jsonlData.StartTime.HasValue
                    ? jsonlData.EndTime.Value - jsonlData.StartTime.Value
                    : TimeSpan.Zero;
                
                // Try to extract model from agent name (e.g., "gpt-5.1-codex")
                string? model = null;
                if (!string.IsNullOrEmpty(agentName))
                {
                    var modelPatterns = new[] { "gpt-", "claude-", "sonnet-", "haiku-", "opus-" };
                    foreach (var pattern in modelPatterns)
                    {
                        var idx = agentName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var rest = agentName.Substring(idx);
                            var parts = rest.Split([' ', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0)
                            {
                                model = parts[0];
                                break;
                            }
                        }
                    }
                }
                
                return new FileStats(
                    FilePath: filePath,
                    Format: isClaude ? "claude" : "copilot",
                    Model: model,
                    TaskName: null,
                    TaskId: null,
                    Status: null,
                    TurnCount: userMsgCount + assistantMsgCount,
                    ToolCallCount: toolCallCount,
                    ErrorCount: errorCount,
                    DurationSeconds: duration.TotalSeconds,
                    AggregateScore: null,
                    Passed: null,
                    ToolUsage: toolUsage,
                    Agent: agentName
                );
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to parse {filePath}: {ex.Message}");
        }
        
        return null;
    }

    public void OutputStatsReport(List<FileStats> stats, string? groupBy, bool asJson, int? failThreshold)
    {
        if (asJson)
        {
            // JSON output
            var summary = new
            {
                total_files = stats.Count,
                total_with_pass_status = stats.Count(s => s.Passed.HasValue),
                passed = stats.Count(s => s.Passed == true),
                failed = stats.Count(s => s.Passed == false),
                pass_rate = stats.Any(s => s.Passed.HasValue) 
                    ? stats.Count(s => s.Passed == true) * 100.0 / stats.Count(s => s.Passed.HasValue) 
                    : (double?)null,
                avg_duration_seconds = stats.Count > 0 ? stats.Average(s => s.DurationSeconds) : 0.0,
                avg_turns = stats.Count > 0 ? stats.Average(s => (double)s.TurnCount) : 0.0,
                avg_tool_calls = stats.Count > 0 ? stats.Average(s => (double)s.ToolCallCount) : 0.0,
                files = stats.Select(s => new
                {
                    file = s.FilePath,
                    format = s.Format,
                    model = s.Model,
                    task = s.TaskName,
                    status = s.Status,
                    passed = s.Passed,
                    duration_seconds = s.DurationSeconds,
                    turns = s.TurnCount,
                    tool_calls = s.ToolCallCount,
                    errors = s.ErrorCount,
                    score = s.AggregateScore
                }).ToArray()
            };
            
            if (groupBy != null)
            {
                if (groupBy == "model")
                {
                    var byModel = stats.GroupBy(s => s.Model ?? "(unknown)").Select(g => new
                    {
                        model = g.Key,
                        count = g.Count(),
                        passed = g.Count(s => s.Passed == true),
                        failed = g.Count(s => s.Passed == false),
                        pass_rate = g.Any(s => s.Passed.HasValue) ? g.Count(s => s.Passed == true) * 100.0 / g.Count(s => s.Passed.HasValue) : (double?)null,
                        avg_duration = g.Average(s => s.DurationSeconds),
                        avg_tool_calls = g.Average(s => s.ToolCallCount)
                    }).OrderBy(x => x.model).ToArray();
                    
                    Console.WriteLine(JsonSerializer.Serialize(new { summary, by_model = byModel }, ColorHelper.SummarySerializer));
                }
                else if (groupBy == "task")
                {
                    var byTask = stats.GroupBy(s => s.TaskName ?? "(unknown)").Select(g => new
                    {
                        task = g.Key,
                        count = g.Count(),
                        passed = g.Count(s => s.Passed == true),
                        failed = g.Count(s => s.Passed == false),
                        pass_rate = g.Any(s => s.Passed.HasValue) ? g.Count(s => s.Passed == true) * 100.0 / g.Count(s => s.Passed.HasValue) : (double?)null,
                        avg_duration = g.Average(s => s.DurationSeconds),
                        avg_tool_calls = g.Average(s => s.ToolCallCount)
                    }).OrderBy(x => x.task).ToArray();
                    
                    Console.WriteLine(JsonSerializer.Serialize(new { summary, by_task = byTask }, ColorHelper.SummarySerializer));
                }
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(summary, ColorHelper.SummarySerializer));
            }
        }
        else
        {
            // Console output
            Console.WriteLine($"Batch Summary: {stats.Count} files scanned");
            Console.WriteLine();
            Console.WriteLine($"  Total:     {stats.Count} sessions");
            
            var withStatus = stats.Where(s => s.Passed.HasValue).ToList();
            if (withStatus.Count > 0)
            {
                var passed = withStatus.Count(s => s.Passed == true);
                var failed = withStatus.Count - passed;
                var passRate = passed * 100.0 / withStatus.Count;
                
                Console.WriteLine($"  Passed:    {passed} ({passRate:F1}%)");
                Console.WriteLine($"  Failed:    {failed} ({100 - passRate:F1}%)");
            }
            
            if (stats.Count > 0)
            {
                var avgDur = stats.Average(s => s.DurationSeconds);
                var minDur = stats.Min(s => s.DurationSeconds);
                var maxDur = stats.Max(s => s.DurationSeconds);
                Console.WriteLine($"  Duration:  avg {avgDur:F1}s (min {minDur:F1}s, max {maxDur:F1}s)");
                Console.WriteLine($"  Tools:     avg {stats.Average(s => s.ToolCallCount):F1} per session");
            }
            Console.WriteLine();
            
            if (groupBy == "model")
            {
                Console.WriteLine("  By Model:");
                Console.WriteLine($"  {"Model",-20}  {"Count",5}  {"Passed",6}  {"Failed",6}  {"Pass Rate",9}  {"Avg Tools",9}");
                
                foreach (var g in stats.GroupBy(s => s.Model ?? "(unknown)").OrderBy(g => g.Key))
                {
                    var gWithStatus = g.Where(s => s.Passed.HasValue).ToList();
                    var gPassed = gWithStatus.Count(s => s.Passed == true);
                    var gFailed = gWithStatus.Count - gPassed;
                    var gPassRate = gWithStatus.Count > 0 ? gPassed * 100.0 / gWithStatus.Count : 0;
                    var gAvgTools = g.Average(s => s.ToolCallCount);
                    
                    var passRateStr = gWithStatus.Count > 0 ? $"{gPassRate:F1}%" : "N/A";
                    Console.WriteLine($"  {g.Key,-20}  {g.Count(),5}  {gPassed,6}  {gFailed,6}  {passRateStr,9}  {gAvgTools,9:F1}");
                }
            }
            else if (groupBy == "task")
            {
                Console.WriteLine("  By Task:");
                Console.WriteLine($"  {"Task",-30}  {"Count",5}  {"Passed",6}  {"Failed",6}  {"Pass Rate",9}  {"Avg Tools",9}");
                
                foreach (var g in stats.GroupBy(s => s.TaskName ?? "(unknown)").OrderBy(g => g.Key))
                {
                    var gWithStatus = g.Where(s => s.Passed.HasValue).ToList();
                    var gPassed = gWithStatus.Count(s => s.Passed == true);
                    var gFailed = gWithStatus.Count - gPassed;
                    var gPassRate = gWithStatus.Count > 0 ? gPassed * 100.0 / gWithStatus.Count : 0;
                    var gAvgTools = g.Average(s => s.ToolCallCount);
                    
                    var passRateStr = gWithStatus.Count > 0 ? $"{gPassRate:F1}%" : "N/A";
                    var taskDisplay = g.Key.Length > 30 ? g.Key.Substring(0, 27) + "..." : g.Key;
                    Console.WriteLine($"  {taskDisplay,-30}  {g.Count(),5}  {gPassed,6}  {gFailed,6}  {passRateStr,9}  {gAvgTools,9:F1}");
                }
            }
        }
        
        // Check fail threshold
        if (failThreshold.HasValue)
        {
            var withStatus = stats.Where(s => s.Passed.HasValue).ToList();
            if (withStatus.Count > 0)
            {
                var passRate = withStatus.Count(s => s.Passed == true) * 100.0 / withStatus.Count;
                if (passRate < failThreshold.Value)
                {
                    if (!asJson)
                        Console.Error.WriteLine($"\nFAIL: Pass rate {passRate:F1}% is below threshold {failThreshold.Value}%");
                    Environment.Exit(1);
                }
            }
        }
    }
}
