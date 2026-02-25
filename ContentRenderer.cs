using System.Text.Json;
using Spectre.Console;
using static TextUtils;

class ContentRenderer(ColorHelper colors, MarkdownRenderer mdRenderer)
{
    string Blue(string s) => colors.Blue(s);
    string Green(string s) => colors.Green(s);
    string Yellow(string s) => colors.Yellow(s);
    string Red(string s) => colors.Red(s);
    string Dim(string s) => colors.Dim(s);
    string Bold(string s) => colors.Bold(s);
    string Cyan(string s) => colors.Cyan(s);
    string Separator() => colors.Separator();
    string Truncate(string s, int max) => colors.Truncate(s, max);

    public List<string> FormatJsonProperties(JsonElement obj, string linePrefix, int maxValueLen)
    {
        List<string> lines = [];
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                var val = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.GetRawText();
                var truncated = Truncate(val, maxValueLen);
                foreach (var segment in SplitLines(truncated))
                    lines.Add($"{linePrefix}{prop.Name}: {segment}");
            }
        }
        else if (obj.ValueKind == JsonValueKind.String)
        {
            var val = obj.GetString() ?? "";
            var truncated = Truncate(val, maxValueLen);
            foreach (var segment in SplitLines(truncated))
                lines.Add($"{linePrefix}{segment}");
        }
        else
        {
            lines.Add($"{linePrefix}{Truncate(obj.GetRawText(), maxValueLen)}");
        }
        return lines;
    }

    // ========== Info bar builders (compact 1-line header) ==========

    public string BuildJsonlInfoBar(JsonlData d)
    {
        List<string> parts = [];
        if (d.SessionId != "") parts.Add($"session {d.SessionId}");
        if (d.CopilotVersion != "") parts.Add(d.CopilotVersion);
        parts.Add($"{d.EventCount} events");
        return $"[{string.Join(" | ", parts)}]";
    }

    public string BuildWazaInfoBar(WazaData d, string filePath)
    {
        List<string> parts = [];
        if (d.TaskId != "") parts.Add(d.TaskId);
        else if (d.TaskName != "") parts.Add(d.TaskName);
        if (d.ModelId != "") parts.Add(d.ModelId);
        if (d.DurationMs > 0) parts.Add($"{d.DurationMs / 1000.0:F0}s");
        double avgScore = d.AggregateScore > 0 ? d.AggregateScore
            : d.Validations.Count > 0 ? d.Validations.Average(v => v.score) : 0;
        if (avgScore > 0) parts.Add($"score: {avgScore:F2}");
        if (d.ToolCallCount > 0) parts.Add($"{d.ToolCallCount} tool calls");
        if (d.Status != "")
        {
            var statusIcon = d.Status.ToLowerInvariant() == "passed" ? "✅" : "❌";
            parts.Add($"{statusIcon} {d.Status}");
        }
        return parts.Count > 0 ? $"[{string.Join(" | ", parts)}]" : $"[{Path.GetFileName(filePath)}]";
    }

    public string BuildEvalInfoBar(EvalData d)
    {
        var passed = d.TotalPassed > 0 ? $"✅{d.TotalPassed}" : "";
        var failed = d.TotalFailed > 0 ? $" ❌{d.TotalFailed}" : "";
        var skipped = d.TotalSkipped > 0 ? $" ⏭{d.TotalSkipped}" : "";
        var inProgress = d.CurrentCase is not null ? $" ⏳{d.CurrentCase}" : "";
        var dur = d.TotalDurationMs > 0 ? $" {d.TotalDurationMs / 1000.0:F1}s" : "";
        return $" {d.Suite} {passed}{failed}{skipped}{inProgress}{dur}";
    }

    // ========== Full header builders (for 'i' overlay) ==========

    public List<string> RenderJsonlHeaderLines(JsonlData d)
    {
        List<string> lines = [];
        int boxWidth = Math.Max(40, AnsiConsole.Profile.Width);
        int inner = boxWidth - 2;
        string top = Bold(Cyan("╭" + new string('─', inner) + "╮"));
        string mid = Bold(Cyan("├" + new string('─', inner) + "┤"));
        string bot = Bold(Cyan("╰" + new string('─', inner) + "╯"));
        string Row(string content) => Bold(Cyan("│")) + PadVisible(content, inner) + Bold(Cyan("│"));

        lines.Add("");
        lines.Add(top);
        lines.Add(Row(Bold("  📋 Copilot CLI Session Log")));
        lines.Add(mid);
        if (d.SessionId != "") lines.Add(Row($"  Session:  {Dim(d.SessionId)}"));
        if (d.StartTime.HasValue) lines.Add(Row($"  Started:  {Dim(d.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"))}"));
        if (d.Branch != "") lines.Add(Row($"  Branch:   {Dim(d.Branch)}"));
        if (d.CopilotVersion != "") lines.Add(Row($"  Version:  {Dim(d.CopilotVersion)}"));
        var duration = (d.StartTime.HasValue && d.EndTime.HasValue) ? d.EndTime.Value - d.StartTime.Value : TimeSpan.Zero;
        lines.Add(Row($"  Events:   {Dim(d.EventCount.ToString())}"));
        lines.Add(Row($"  Duration: {Dim(FormatRelativeTime(duration))}"));
        lines.Add(bot);
        lines.Add("");
        return lines;
    }

    public List<string> RenderEvalHeaderLines(EvalData d)
    {
        List<string> lines = [];
        int boxWidth = Math.Max(40, AnsiConsole.Profile.Width);
        int inner = boxWidth - 2;
        string top = Bold(Cyan("╭" + new string('─', inner) + "╮"));
        string mid = Bold(Cyan("├" + new string('─', inner) + "┤"));
        string bot = Bold(Cyan("╰" + new string('─', inner) + "╯"));
        string Row(string content) => Bold(Cyan("│")) + PadVisible(content, inner) + Bold(Cyan("│"));

        lines.Add("");
        lines.Add(top);
        lines.Add(Row(Bold("  📋 Eval Suite: " + d.Suite)));
        lines.Add(mid);
        if (d.Description != "") lines.Add(Row($"  {Dim(d.Description.TrimEnd())}"));
        lines.Add(Row($"  Cases:    {Bold(d.Cases.Count.ToString())}/{d.CaseCount}  " +
            $"{Green("✅" + d.TotalPassed)} {Red("❌" + d.TotalFailed)} {Dim("⏭" + d.TotalSkipped)}"));
        if (d.TotalDurationMs > 0)
            lines.Add(Row($"  Duration: {Bold($"{d.TotalDurationMs / 1000.0:F1}s")}  Tools: {d.TotalToolCalls}"));
        lines.Add(bot);
        lines.Add("");
        return lines;
    }

    public List<string> RenderEvalContentLines(EvalData d, string? filter, bool expandTool)
    {
        List<string> lines = [];

        for (int i = 0; i < d.Cases.Count; i++)
        {
            var c = d.Cases[i];
            var badge = c.Passed switch
            {
                true => Green("✅ PASS"),
                false => Red("❌ FAIL"),
                null => Yellow("⏳ RUNNING")
            };
            var durStr = c.DurationMs > 0 ? Dim($" ({c.DurationMs / 1000.0:F1}s)") : "";

            lines.Add(Bold($"━━━ Case {i + 1}: {c.Name} {badge}{durStr}"));
            lines.Add("");

            // Prompt
            lines.Add(Dim("  Prompt:"));
            foreach (var pl in SplitLines(c.Prompt))
                lines.Add(Cyan("    " + pl));
            lines.Add("");

            // Tool calls
            if (c.ToolEvents.Count > 0 || c.ToolsUsed.Count > 0)
            {
                lines.Add(Dim($"  Tools ({c.ToolCallCount}):"));
                if (expandTool || c.ToolEvents.Count <= 6)
                {
                    foreach (var (tool, id, dur) in c.ToolEvents)
                        lines.Add($"    {Yellow("⚡")} {tool} {Dim($"({dur:F0}ms)")}");
                }
                else
                {
                    lines.Add($"    {string.Join(", ", c.ToolsUsed.Select(t => Yellow(t)))}");
                }
                lines.Add("");
            }

            // Response
            var response = c.MessageAccumulator.ToString();
            if (response.Length > 0)
            {
                if (filter is not null && !response.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(Dim($"  Response: ({response.Length} chars) [filtered out]"));
                }
                else
                {
                    lines.Add(Dim("  Response:"));
                    var respLines = SplitLines(response);
                    var maxLines = expandTool ? respLines.Length : Math.Min(respLines.Length, 20);
                    for (int j = 0; j < maxLines; j++)
                        lines.Add("    " + respLines[j]);
                    if (maxLines < respLines.Length)
                        lines.Add(Dim($"    ... ({respLines.Length - maxLines} more lines, press 't' to expand)"));
                }
                lines.Add("");
            }

            // Assertion feedback
            if (c.AssertionFeedback is not null)
            {
                lines.Add(c.Passed == true
                    ? Green($"  Assertion: {c.AssertionFeedback}")
                    : Red($"  Assertion: {c.AssertionFeedback}"));
                lines.Add("");
            }

            // Error
            if (c.Error is not null)
            {
                lines.Add(Red($"  ⚠ Error: {c.Error}"));
                lines.Add("");
            }
        }

        return lines;
    }

    public List<string> RenderWazaHeaderLines(WazaData d)
    {
        List<string> lines = [];
        double avgScore = d.Validations.Count > 0 ? d.Validations.Average(v => v.score) : 0;
        int boxWidth = Math.Max(40, AnsiConsole.Profile.Width);
        int inner = boxWidth - 2;
        string top = Bold(Cyan("╭" + new string('─', inner) + "╮"));
        string mid = Bold(Cyan("├" + new string('─', inner) + "┤"));
        string bot = Bold(Cyan("╰" + new string('─', inner) + "╯"));
        string Row(string content) => Bold(Cyan("│")) + PadVisible(content, inner) + Bold(Cyan("│"));

        lines.Add("");
        lines.Add(top);
        lines.Add(Row(Bold("  🧪 Waza Eval Transcript")));
        lines.Add(mid);
        if (d.TaskName != "") lines.Add(Row($"  Task:     {Bold(d.TaskName)}"));
        if (d.TaskId != "") lines.Add(Row($"  ID:       {Dim(d.TaskId)}"));
        if (d.Status != "")
        {
            var statusStr = d.Status.ToLowerInvariant() switch
            {
                "passed" => Green("✅ PASS"),
                "failed" => Red("❌ FAIL"),
                _ => Red($"⚠ {d.Status.ToUpperInvariant()}")
            };
            lines.Add(Row($"  Status:   {statusStr}"));
        }
        if (d.Validations.Count > 0)
        {
            Func<string, string> scoreFn = avgScore >= 0.7 ? Green : Red;
            lines.Add(Row($"  Score:    {scoreFn($"{avgScore:P0}")}"));
        }
        if (d.DurationMs > 0)
            lines.Add(Row($"  Duration: {Dim(FormatRelativeTime(TimeSpan.FromMilliseconds(d.DurationMs)))}"));
        if (d.ToolCallCount > 0)
            lines.Add(Row($"  Tools:    {Dim($"{d.ToolCallCount} calls")}"));
        if (d.TokensIn > 0 || d.TokensOut > 0)
            lines.Add(Row($"  Tokens:   {Dim($"in={d.TokensIn}, out={d.TokensOut}")}"));
        lines.Add(bot);

        if (d.Validations.Count > 0)
        {
            lines.Add("");
            lines.Add(Bold("  Validations:"));
            foreach (var (name, score, passed, feedback) in d.Validations)
            {
                var icon = passed ? Green("✓") : Red("✗");
                Func<string, string> valScoreFn = score >= 0.7 ? Green : Red;
                lines.Add($"    {icon} {name}: {valScoreFn($"{score:P0}")}");
                if (!string.IsNullOrEmpty(feedback))
                    lines.Add(Dim($"      {feedback}"));
            }
        }
        lines.Add("");
        return lines;
    }

    // ========== Content line builders ==========

    public List<string> RenderJsonlContentLines(JsonlData d, string? filter, bool expandTool)
    {
        List<string> lines = [];
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

        var turnList = filtered.ToList();
        if (turnList.Count == 0) { lines.Add(Dim("  No matching events.")); return lines; }

        foreach (var (type, root, ts) in turnList)
        {
            var relTime = (ts.HasValue && d.StartTime.HasValue) ? FormatRelativeTime(ts.Value - d.StartTime.Value) : "";
            var margin = Dim($"  {relTime,10}  ");
            var data = root.TryGetProperty("data", out var d2) ? d2 : default;

            switch (type)
            {
                case "user.message":
                {
                    lines.Add(Separator());
                    var content = SafeGetString(data, "content");
                    var isQueued = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("queued", out var q) && q.ValueKind == JsonValueKind.True;
                    var userLabel = isQueued ? "┃ USER (queued)" : "┃ USER";
                    lines.Add(margin + Blue(userLabel));
                    foreach (var line in SplitLines(content))
                        lines.Add(margin + Blue($"┃ {line}"));
                    break;
                }
                case "assistant.message":
                {
                    lines.Add(Separator());
                    var content = SafeGetString(data, "content");
                    lines.Add(margin + Green("┃ ASSISTANT"));
                    if (!string.IsNullOrEmpty(content))
                        foreach (var line in mdRenderer.RenderLines(content, "green"))
                            lines.Add(margin + line);
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("toolRequests", out var toolReqs)
                        && toolReqs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tr in toolReqs.EnumerateArray())
                        {
                            var tn = SafeGetString(tr, "toolName");
                            if (string.IsNullOrEmpty(tn) && tr.TryGetProperty("function", out var fn))
                                tn = SafeGetString(fn, "name");
                            lines.Add(margin + Yellow($"┃ 🔧 Tool request: {tn}"));
                        }
                    }
                    // Copilot CLI reasoning (thinking)
                    if (expandTool && data.ValueKind == JsonValueKind.Object)
                    {
                        var reasoning = SafeGetString(data, "reasoningText");
                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            lines.Add(margin + Dim("┃ 💭 Thinking:"));
                            foreach (var line in SplitLines(reasoning))
                                lines.Add(margin + Dim($"┃   {line}"));
                        }
                    }
                    break;
                }
                case "assistant.thinking":
                {
                    if (!expandTool) break;
                    var content = SafeGetString(data, "content");
                    if (string.IsNullOrEmpty(content)) break;
                    lines.Add(margin + Dim("┃ 💭 THINKING"));
                    foreach (var line in SplitLines(content))
                        lines.Add(margin + Dim($"┃   {line}"));
                    break;
                }
                case "tool.execution_start":
                {
                    var toolName = SafeGetString(data, "toolName");
                    var toolContext = "";
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("arguments", out var toolArgs))
                    {
                        toolContext = toolName switch
                        {
                            "Read" or "Write" or "Edit" or "MultiEdit" =>
                                toolArgs.TryGetProperty("file_path", out var fp) ? Path.GetFileName(fp.GetString() ?? "") : "",
                            "Bash" =>
                                toolArgs.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" :
                                toolArgs.TryGetProperty("command", out var cmd) ? Truncate(cmd.GetString() ?? "", 60) : "",
                            "Glob" or "Grep" =>
                                toolArgs.TryGetProperty("pattern", out var pat) ? pat.GetString() ?? "" : "",
                            "Task" =>
                                toolArgs.TryGetProperty("description", out var td) ? td.GetString() ?? "" : "",
                            _ => ""
                        };
                    }
                    var toolLabel = string.IsNullOrEmpty(toolContext) ? $"TOOL: {toolName}" : $"TOOL: {toolName} — {toolContext}";
                    lines.Add(margin + Yellow($"┃ {toolLabel}"));
                    if (expandTool && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("arguments", out var toolArgs2))
                    {
                        lines.Add(margin + Dim("┃   Args:"));
                        foreach (var pl in FormatJsonProperties(toolArgs2, "┃     ", 500))
                            lines.Add(margin + Dim(pl));
                    }
                    break;
                }
                case "tool.result":
                {
                    if (data.ValueKind != JsonValueKind.Object) break;
                    var status = "";
                    var resultContent = "";
                    if (data.TryGetProperty("result", out var res))
                    {
                        status = SafeGetString(res, "status");
                        if (res.TryGetProperty("content", out var rc))
                            resultContent = ExtractContentString(rc);
                    }
                    var isError = status == "error";
                    var isRejected = isError && !string.IsNullOrEmpty(resultContent) &&
                        (resultContent.Contains("The user doesn't want to proceed") ||
                         resultContent.Contains("Request interrupted by user") ||
                         resultContent.Contains("[Request interrupted by user for tool use]"));
                    var colorFn = isRejected ? (Func<string, string>)Yellow : isError ? (Func<string, string>)Red : Dim;
                    var resultLabel = isRejected ? "┃ ⚠️ Rejected:" : isError ? "┃ ❌ ERROR:" : "┃ ✅ Result";
                    var resultSummary = resultLabel;
                    if (!expandTool && !string.IsNullOrEmpty(resultContent))
                        resultSummary += $" ({resultContent.Length:N0} chars)";
                    lines.Add(margin + colorFn(resultSummary));
                    if (expandTool && !string.IsNullOrEmpty(resultContent))
                    {
                        if (resultContent.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                            lines.Add(margin + colorFn($"┃   [binary content, {resultContent.Length} bytes]"));
                        else
                        {
                            lines.Add(margin + colorFn("┃"));
                            var truncated = Truncate(resultContent, 500);
                            foreach (var line in SplitLines(truncated).Take(colors.Full ? int.MaxValue : 20))
                                lines.Add(margin + colorFn($"┃   {line}"));
                        }
                    }
                    break;
                }
            }
        }
        lines.Add("");
        return lines;
    }

    public List<string> RenderWazaContentLines(WazaData d, string? filter, bool expandTool)
    {
        List<string> lines = [];
        if (d.TranscriptItems.Length == 0) { lines.Add(Dim("  No events found")); return lines; }

        // Pre-scan: build tool_call_id → tool_name map from execution_start events
        Dictionary<string, string> toolCallNames = [];
        foreach (var ti in d.TranscriptItems)
        {
            if (SafeGetString(ti, "type").Equals("tool.execution_start", StringComparison.OrdinalIgnoreCase))
            {
                var tcId = SafeGetString(ti, "tool_call_id");
                var tcName = SafeGetString(ti, "tool_name");
                if (!string.IsNullOrEmpty(tcId) && !string.IsNullOrEmpty(tcName))
                    toolCallNames[tcId] = tcName;
            }
        }

        int turnIndex = 0;
        int messageIndex = 0;
        foreach (var item in d.TranscriptItems)
        {
            var itemType = SafeGetString(item, "type").ToLowerInvariant();
            var content = SafeGetString(item, "content");
            if (string.IsNullOrEmpty(content)) content = SafeGetString(item, "message");
            var toolName = SafeGetString(item, "tool_name");
            if (string.IsNullOrEmpty(toolName))
            {
                var callId = SafeGetString(item, "tool_call_id");
                if (!string.IsNullOrEmpty(callId) && toolCallNames.TryGetValue(callId, out var mapped))
                    toolName = mapped;
            }

            bool isUserMessage = itemType is "user" or "user_message" or "user.message" or "human"
                || (itemType == "message" && messageIndex == 0);
            bool isAssistantMessage = itemType is "assistant" or "assistant_message" or "assistant.message" or "ai"
                || (itemType == "message" && messageIndex > 0);
            bool isToolEvent = itemType is "tool" or "tool_call" or "tool_result" or "function"
                or "toolexecutionstart" or "toolexecutioncomplete"
                or "tool.execution_start" or "tool.execution_complete" or "tool.execution_partial_result"
                or "skill.invoked";

            if (itemType == "message") messageIndex++;

            if (filter is not null)
            {
                var match = filter switch
                {
                    "user" => isUserMessage,
                    "assistant" => isAssistantMessage,
                    "tool" => isToolEvent,
                    "error" => item.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False,
                    _ => true
                };
                if (!match) continue;
            }

            // Skip partial_result heartbeat events
            if (itemType == "tool.execution_partial_result") continue;

            turnIndex++;
            var margin = Dim($"  {turnIndex,5}  ");
            lines.Add(Separator());

            if (isUserMessage)
            {
                lines.Add(margin + Blue("┃ USER"));
                foreach (var line in SplitLines(content))
                    lines.Add(margin + Blue($"┃ {line}"));
            }
            else if (isAssistantMessage)
            {
                lines.Add(margin + Green("┃ ASSISTANT"));
                foreach (var line in mdRenderer.RenderLines(content, "green"))
                    lines.Add(margin + line);
            }
            else if (isToolEvent)
            {
                var isSuccess = !item.TryGetProperty("success", out var sv) || sv.ValueKind != JsonValueKind.False;
                var colorFn = isSuccess ? (Func<string, string>)Yellow : Red;
                var label = !string.IsNullOrEmpty(toolName) ? $"TOOL: {toolName}" : "TOOL";
                lines.Add(margin + colorFn($"┃ {label}"));
                if (expandTool)
                {
                    if (item.TryGetProperty("arguments", out var toolArgs) && toolArgs.ValueKind != JsonValueKind.Null)
                    {
                        lines.Add(margin + Dim("┃   Args:"));
                        foreach (var pl in FormatJsonProperties(toolArgs, "┃     ", 500))
                            lines.Add(margin + Dim(pl));
                    }
                    if (item.TryGetProperty("tool_result", out var toolRes) && toolRes.ValueKind != JsonValueKind.Null)
                    {
                        var resStr = ExtractContentString(toolRes);
                        if (resStr.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                            lines.Add(margin + Dim($"┃   [binary content, {resStr.Length} bytes]"));
                        else
                        {
                            var truncated = Truncate(resStr, 500);
                            var resLines = SplitLines(truncated).Take(colors.Full ? int.MaxValue : 20).ToArray();
                            if (resLines.Length > 0)
                                lines.Add(margin + Dim($"┃   Result: {resLines[0]}"));
                            foreach (var line in resLines.Skip(1))
                                lines.Add(margin + Dim($"┃   {line}"));
                        }
                    }
                    if (!string.IsNullOrEmpty(content))
                        lines.Add(margin + Dim($"┃   {Truncate(content, 500)}"));
                }
                else if (itemType == "tool.execution_complete" && item.TryGetProperty("tool_result", out var collapsedRes) && collapsedRes.ValueKind != JsonValueKind.Null)
                {
                    var charCount = ExtractContentString(collapsedRes).Length;
                    lines.Add(margin + Dim($"┃   ({charCount} chars)"));
                }
                if (!isSuccess)
                    lines.Add(margin + Red("┃ ❌ Failed"));
            }
            else
            {
                lines.Add(margin + Dim($"┃ {itemType}"));
                if (!string.IsNullOrEmpty(content))
                    lines.Add(margin + Dim($"┃ {Truncate(content, 200)}"));
            }
        }
        lines.Add("");
        return lines;
    }
}
