using System.Globalization;
using System.Text.Json;
using static TextUtils;

static class EvalProcessor
{
    public static bool IsClaudeFormat(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(10))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var evType = SafeGetString(root, "type");
                if (evType is "user" or "assistant")
                {
                    if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("role", out _))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }

    public static bool IsEvalFormat(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (firstLine is null) return false;
            var doc = JsonDocument.Parse(firstLine);
            var root = doc.RootElement;
            return SafeGetString(root, "type") == "eval.start" && root.TryGetProperty("seq", out _);
        }
        catch { return false; }
    }

    public static EvalData? ParseEvalData(string path)
    {
        var eval = new EvalData();
        EvalCaseResult? current = null;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            var root = doc.RootElement;
            var evType = SafeGetString(root, "type");
            var data = root.TryGetProperty("data", out var d) ? d : default;
            if (data.ValueKind == JsonValueKind.Undefined) continue;

            switch (evType)
            {
                case "eval.start":
                    eval.Suite = SafeGetString(data, "suite");
                    eval.Description = SafeGetString(data, "description");
                    if (data.TryGetProperty("case_count", out var cc) && cc.TryGetInt32(out var ccv))
                        eval.CaseCount = ccv;
                    break;

                case "case.start":
                    current = new EvalCaseResult
                    {
                        Name = SafeGetString(data, "case"),
                        Prompt = SafeGetString(data, "prompt")
                    };
                    eval.CurrentCase = current.Name;
                    eval.Cases.Add(current);
                    break;

                case "message":
                    if (current is not null)
                        current.MessageAccumulator.Append(SafeGetString(data, "content"));
                    break;

                case "tool.start":
                    if (current is not null)
                    {
                        var toolName = SafeGetString(data, "tool_name");
                        var toolId = SafeGetString(data, "tool_call_id");
                        var tsStr = SafeGetString(root, "ts");
                        if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var toolTs))
                            current.PendingTools[toolId] = toolTs;
                        if (!current.ToolsUsed.Contains(toolName))
                            current.ToolsUsed.Add(toolName);
                    }
                    break;

                case "tool.complete":
                    if (current is not null)
                    {
                        var tcName = SafeGetString(data, "tool_name");
                        var tcId = SafeGetString(data, "tool_call_id");
                        double durMs = 0;
                        if (data.TryGetProperty("duration_ms", out var dur))
                            dur.TryGetDouble(out durMs);
                        current.ToolEvents.Add((tcName, tcId, durMs));
                        current.PendingTools.Remove(tcId);
                    }
                    break;

                case "assertion.result":
                    if (current is not null)
                    {
                        var fb = SafeGetString(data, "feedback");
                        current.AssertionFeedback = string.IsNullOrEmpty(current.AssertionFeedback)
                            ? fb : current.AssertionFeedback + "; " + fb;
                    }
                    break;

                case "case.complete":
                    if (current is not null)
                    {
                        if (data.TryGetProperty("passed", out var p))
                            current.Passed = p.GetBoolean();
                        if (data.TryGetProperty("duration_ms", out var dm))
                        { dm.TryGetDouble(out var dmv2); current.DurationMs = dmv2; }
                        if (data.TryGetProperty("tool_call_count", out var tcc) && tcc.TryGetInt32(out var tccv))
                            current.ToolCallCount = tccv;
                        if (data.TryGetProperty("response_length", out var rl) && rl.TryGetInt32(out var rlv))
                            current.ResponseLength = rlv;
                    }
                    eval.CurrentCase = null;
                    current = null;
                    break;

                case "eval.complete":
                    if (data.TryGetProperty("passed", out var ep) && ep.TryGetInt32(out var epv))
                        eval.TotalPassed = epv;
                    if (data.TryGetProperty("failed", out var ef) && ef.TryGetInt32(out var efv))
                        eval.TotalFailed = efv;
                    if (data.TryGetProperty("skipped", out var es) && es.TryGetInt32(out var esv))
                        eval.TotalSkipped = esv;
                    if (data.TryGetProperty("total_duration_ms", out var etd))
                    { etd.TryGetDouble(out var etdv2); eval.TotalDurationMs = etdv2; }
                    if (data.TryGetProperty("total_tool_calls", out var etc2) && etc2.TryGetInt32(out var etc2v))
                        eval.TotalToolCalls = etc2v;
                    break;

                case "error":
                    if (current is not null)
                        current.Error = SafeGetString(data, "message");
                    break;
            }
        }

        // If eval.complete wasn't emitted, compute from cases
        if (eval.TotalPassed == 0 && eval.TotalFailed == 0 && eval.Cases.Count > 0)
        {
            eval.TotalPassed = eval.Cases.Count(c => c.Passed == true);
            eval.TotalFailed = eval.Cases.Count(c => c.Passed == false);
            eval.TotalDurationMs = eval.Cases.Sum(c => c.DurationMs);
            eval.TotalToolCalls = eval.Cases.Sum(c => c.ToolCallCount);
        }

        return eval.Cases.Count > 0 || eval.Suite.Length > 0 ? eval : null;
    }

    public static void ProcessEvalEvent(EvalData eval, string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { return; }

        var root = doc.RootElement;
        var evType = SafeGetString(root, "type");
        var data = root.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Undefined) return;

        var currentCase = eval.Cases.LastOrDefault(c => c.Name == eval.CurrentCase);

        switch (evType)
        {
            case "case.start":
                var newCase = new EvalCaseResult
                {
                    Name = SafeGetString(data, "case"),
                    Prompt = SafeGetString(data, "prompt")
                };
                eval.CurrentCase = newCase.Name;
                eval.Cases.Add(newCase);
                break;

            case "message":
                currentCase?.MessageAccumulator.Append(SafeGetString(data, "content"));
                break;

            case "tool.start":
                if (currentCase is not null)
                {
                    var toolName = SafeGetString(data, "tool_name");
                    var toolId = SafeGetString(data, "tool_call_id");
                    if (!currentCase.ToolsUsed.Contains(toolName))
                        currentCase.ToolsUsed.Add(toolName);
                }
                break;

            case "tool.complete":
                if (currentCase is not null)
                {
                    var tcName = SafeGetString(data, "tool_name");
                    var tcId = SafeGetString(data, "tool_call_id");
                    double durMs = 0;
                    if (data.TryGetProperty("duration_ms", out var dur))
                        dur.TryGetDouble(out durMs);
                    currentCase.ToolEvents.Add((tcName, tcId, durMs));
                }
                break;

            case "assertion.result":
                if (currentCase is not null)
                {
                    var fb = SafeGetString(data, "feedback");
                    currentCase.AssertionFeedback = string.IsNullOrEmpty(currentCase.AssertionFeedback)
                        ? fb : currentCase.AssertionFeedback + "; " + fb;
                }
                break;

            case "case.complete":
                if (currentCase is not null)
                {
                    if (data.TryGetProperty("passed", out var p))
                        currentCase.Passed = p.GetBoolean();
                    if (data.TryGetProperty("duration_ms", out var dm))
                    { dm.TryGetDouble(out var dmv3); currentCase.DurationMs = dmv3; }
                    if (data.TryGetProperty("tool_call_count", out var tcc) && tcc.TryGetInt32(out var tccv))
                        currentCase.ToolCallCount = tccv;
                    if (data.TryGetProperty("response_length", out var rl) && rl.TryGetInt32(out var rlv))
                        currentCase.ResponseLength = rlv;
                }
                eval.CurrentCase = null;
                break;

            case "eval.complete":
                if (data.TryGetProperty("passed", out var ep) && ep.TryGetInt32(out var epv))
                    eval.TotalPassed = epv;
                if (data.TryGetProperty("failed", out var ef) && ef.TryGetInt32(out var efv))
                    eval.TotalFailed = efv;
                if (data.TryGetProperty("skipped", out var es) && es.TryGetInt32(out var esv))
                    eval.TotalSkipped = esv;
                if (data.TryGetProperty("total_duration_ms", out var etd))
                { etd.TryGetDouble(out var etdv3); eval.TotalDurationMs = etdv3; }
                if (data.TryGetProperty("total_tool_calls", out var etc2) && etc2.TryGetInt32(out var etc2v))
                    eval.TotalToolCalls = etc2v;
                break;

            case "error":
                if (currentCase is not null)
                    currentCase.Error = SafeGetString(data, "message");
                break;
        }
    }
}
