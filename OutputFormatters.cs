using System.Text.Json;
using static TextUtils;

class OutputFormatters(ColorHelper colors, bool full)
{
    public void OutputJsonl(JsonlData d, string? filter, bool expandTool)
    {
        var filtered = d.Turns.AsEnumerable();
        if (filter is not null)
        {
            filtered = filtered.Where(t => filter switch
            {
                "user" => t.type == "user.message",
                "assistant" => t.type is "assistant.message" or "assistant.thinking",
                "tool" => t.type is "tool.execution_start" or "tool.result",
                "error" => t.type == "tool.result" && t.root.TryGetProperty("data", out var dd)
                           && dd.TryGetProperty("result", out var r) && SafeGetString(r, "status") == "error",
                _ => true
            });
        }
        
        int turnIndex = 0;
        var turnList = filtered.ToList();
        
        foreach (var (type, root, ts) in turnList)
        {
            var data = root.TryGetProperty("data", out var d2) ? d2 : default;
            
            switch (type)
            {
                case "user.message":
                {
                    var content = SafeGetString(data, "content");
                    var json = new TurnOutput(turnIndex, "user", ts?.ToString("o"), content, content.Length);
                    Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.JsonlSerializer));
                    turnIndex++;
                    break;
                }
                case "assistant.message":
                {
                    var content = SafeGetString(data, "content");
                    var toolCallNames = new List<string>();
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("toolRequests", out var toolReqs)
                        && toolReqs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tr in toolReqs.EnumerateArray())
                        {
                            if (tr.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                toolCallNames.Add(nameEl.GetString() ?? "");
                        }
                    }
                    
                    var json = new TurnOutput(turnIndex, "assistant", ts?.ToString("o"), content, content.Length,
                        tool_calls: toolCallNames.Count > 0 ? toolCallNames.ToArray() : null);
                    Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.JsonlSerializer));
                    turnIndex++;
                    break;
                }
                case "tool.execution_start":
                {
                    var toolName = SafeGetString(data, "name");
                    var args = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("args", out var a) ? a : default;
                    
                    var json = new TurnOutput(turnIndex, "tool", ts?.ToString("o"),
                        tool_name: toolName, status: "start",
                        args: expandTool && args.ValueKind != JsonValueKind.Undefined ? JsonSerializer.Serialize(args) : null);
                    Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.JsonlSerializer));
                    break;
                }
                case "tool.result":
                {
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("result", out var res))
                    {
                        var toolName = SafeGetString(data, "name");
                        var resultStatus = SafeGetString(res, "status");
                        var output = SafeGetString(res, "output");
                        
                        var json = new TurnOutput(turnIndex, "tool", ts?.ToString("o"),
                            tool_name: toolName, status: "complete",
                            result_status: expandTool ? resultStatus : null,
                            result_length: output.Length,
                            result: expandTool ? (full ? output : (output.Length > 500 ? output[..500] + "..." : output)) : null);
                        Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.JsonlSerializer));
                    }
                    break;
                }
            }
        }
    }

    public void OutputWazaJsonl(WazaData d, string? filter, bool expandTool)
    {
        // For waza, output a simplified JSON representation of transcript items
        int turnIndex = 0;
        foreach (var item in d.TranscriptItems)
        {
            if (item.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
            {
                var role = roleEl.GetString();
                var content = item.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
                
                if (filter is not null)
                {
                    bool matches = filter switch
                    {
                        "user" => role == "user",
                        "assistant" => role == "assistant",
                        "tool" => false, // Waza doesn't have separate tool events
                        _ => true
                    };
                    if (!matches) continue;
                }
                
                var json = new TurnOutput(turnIndex, role!, content: content, content_length: content?.Length ?? 0);
                Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.JsonlSerializer));
                turnIndex++;
            }
        }
    }

    public void OutputSummary(JsonlData d, bool asJson)
    {
        // Calculate statistics
        int userMsgCount = 0, assistantMsgCount = 0, toolCallCount = 0, errorCount = 0;
        var toolUsage = new Dictionary<string, int>();
        var skillsInvoked = new HashSet<string>();
        
        foreach (var (type, root, ts) in d.Turns)
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
                                
                                // Detect skill invocations
                                if (toolName == "skill" && tr.TryGetProperty("arguments", out var arguments) 
                                    && arguments.TryGetProperty("skill", out var skillNameEl))
                                {
                                    skillsInvoked.Add(skillNameEl.GetString() ?? "");
                                }
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
        
        // Detect agent name from events
        string agentName = "";
        foreach (var ev in d.Events)
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
        
        var duration = d.EndTime.HasValue && d.StartTime.HasValue 
            ? d.EndTime.Value - d.StartTime.Value 
            : TimeSpan.Zero;
        
        if (asJson)
        {
            var json = new SessionSummary(
                session_id: d.SessionId,
                duration_seconds: (int)duration.TotalSeconds,
                duration_formatted: FormatDuration(duration),
                start_time: d.StartTime?.ToString("o"),
                end_time: d.EndTime?.ToString("o"),
                turns: new TurnCounts(userMsgCount, assistantMsgCount, toolCallCount),
                skills_invoked: skillsInvoked.OrderBy(s => s).ToArray(),
                tools_used: toolUsage.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value),
                agent: agentName,
                errors: errorCount,
                last_activity: d.EndTime?.ToString("o"));
            Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.SummarySerializer));
        }
        else
        {
            Console.WriteLine($"Session: {d.SessionId}");
            Console.WriteLine($"Duration: {FormatDuration(duration)} ({d.StartTime?.ToString("HH:mm:ss")} - {d.EndTime?.ToString("HH:mm:ss")} UTC)");
            Console.WriteLine($"Turns: {userMsgCount} user, {assistantMsgCount} assistant, {toolCallCount} tool calls");
            if (skillsInvoked.Count > 0)
                Console.WriteLine($"Skills invoked: {string.Join(", ", skillsInvoked.OrderBy(s => s))}");
            if (toolUsage.Count > 0)
            {
                var topTools = toolUsage.OrderByDescending(kv => kv.Value).Take(10);
                Console.WriteLine($"Tools used: {string.Join(", ", topTools.Select(kv => $"{kv.Key} ({kv.Value})"))}");
            }
            if (!string.IsNullOrEmpty(agentName))
                Console.WriteLine($"Agent: {agentName}");
            if (errorCount > 0)
                Console.WriteLine($"Errors: {errorCount} tool failures");
            Console.WriteLine($"Last activity: {d.EndTime?.ToString("yyyy-MM-dd HH:mm:ss")} UTC");
        }
    }

    public void OutputWazaSummary(WazaData d, bool asJson)
    {
        if (asJson)
        {
            var json = new WazaSummary(
                task_name: d.TaskName,
                task_id: d.TaskId,
                status: d.Status,
                duration_ms: d.DurationMs,
                duration_formatted: FormatDuration(TimeSpan.FromMilliseconds(d.DurationMs)),
                total_turns: d.TotalTurns,
                tool_calls: d.ToolCallCount,
                tokens: new TokenCounts(d.TokensIn, d.TokensOut, d.TokensIn + d.TokensOut),
                model: d.ModelId,
                aggregate_score: d.AggregateScore,
                validations: d.Validations.Select(v => new ValidationOutput(v.name, v.score, v.passed, v.feedback)).ToArray());
            Console.WriteLine(JsonSerializer.Serialize(json, ColorHelper.SummarySerializer));
        }
        else
        {
            Console.WriteLine($"Task: {d.TaskName} ({d.TaskId})");
            Console.WriteLine($"Status: {d.Status}");
            Console.WriteLine($"Duration: {FormatDuration(TimeSpan.FromMilliseconds(d.DurationMs))}");
            Console.WriteLine($"Turns: {d.TotalTurns}, Tool calls: {d.ToolCallCount}");
            Console.WriteLine($"Tokens: {d.TokensIn} in, {d.TokensOut} out ({d.TokensIn + d.TokensOut} total)");
            Console.WriteLine($"Model: {d.ModelId}");
            Console.WriteLine($"Score: {d.AggregateScore:F2}");
            if (d.Validations.Count > 0)
            {
                Console.WriteLine("Validations:");
                foreach (var v in d.Validations)
                    Console.WriteLine($"  {v.name}: {(v.passed ? "✓" : "✗")} ({v.score:F2})");
            }
        }
    }
}
