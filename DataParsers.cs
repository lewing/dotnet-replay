using System.Globalization;
using System.Text.Json;
using static TextUtils;

class DataParsers(int? tail)
{
    public JsonlData? ParseJsonlData(string path)
    {
        List<JsonDocument> events = [];
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { events.Add(JsonDocument.Parse(line)); }
            catch { /* skip malformed lines */ }
        }
        if (events.Count == 0) { Console.Error.WriteLine("No events found"); return null; }

        string sessionId = "", branch = "", copilotVersion = "", cwd = "";
        DateTimeOffset? startTime = null, endTime = null;

        // Derive session ID from directory name (Copilot session-state uses {guid}/events.jsonl)
        var dirName = Path.GetFileName(Path.GetDirectoryName(path));
        if (!string.IsNullOrEmpty(dirName) && Guid.TryParse(dirName, out _))
            sessionId = dirName;

        foreach (var ev in events)
        {
            var root = ev.RootElement;
            var evType = SafeGetString(root, "type");
            if (evType == "session.start" && root.TryGetProperty("data", out var d))
            {
                if (d.TryGetProperty("context", out var ctx))
                {
                    cwd = SafeGetString(ctx, "cwd");
                    branch = SafeGetString(ctx, "branch");
                }
                copilotVersion = SafeGetString(d, "copilotVersion");
            }
            var id = SafeGetString(root, "id");
            if (!string.IsNullOrEmpty(id) && sessionId == "") sessionId = id.Split('.').FirstOrDefault() ?? id;
            var ts = SafeGetString(root, "timestamp");
            if (DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            {
                startTime ??= dto;
                endTime = dto;
            }
        }

        List<(string type, JsonElement root, DateTimeOffset? ts)> turns = [];
        foreach (var ev in events)
        {
            var root = ev.RootElement;
            var evType = SafeGetString(root, "type");
            var tsStr = SafeGetString(root, "timestamp");
            DateTimeOffset? ts = null;
            if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                ts = dto;
            if (evType is "user.message" or "assistant.message" or "tool.execution_start" or "tool.result")
                turns.Add((evType, root, ts));
        }

        if (tail.HasValue && tail.Value < turns.Count)
            turns = turns.Skip(turns.Count - tail.Value).ToList();

        return new JsonlData(events, turns, sessionId, branch, copilotVersion, cwd, startTime, endTime, events.Count);
    }

    public JsonlData? ParseClaudeData(string path)
    {
        List<JsonDocument> events = [];
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { events.Add(JsonDocument.Parse(line)); }
            catch { }
        }
        if (events.Count == 0) return null;

        string sessionId = "", branch = "", cwd = "", version = "";
        DateTimeOffset? startTime = null, endTime = null;

        List<(string type, JsonElement root, DateTimeOffset? ts)> turns = [];

        foreach (var ev in events)
        {
            var root = ev.RootElement;
            var evType = SafeGetString(root, "type");

            // Extract metadata from any event
            if (sessionId == "") sessionId = SafeGetString(root, "sessionId");
            if (branch == "") branch = SafeGetString(root, "gitBranch");
            if (cwd == "") cwd = SafeGetString(root, "cwd");
            if (version == "") version = SafeGetString(root, "version");

            // Parse timestamp (Claude uses unix milliseconds)
            DateTimeOffset? ts = null;
            if (root.TryGetProperty("timestamp", out var tsEl))
            {
                if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var msTs))
                    ts = DateTimeOffset.FromUnixTimeMilliseconds(msTs);
                else if (tsEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                    ts = dto;
            }
            startTime ??= ts;
            if (ts.HasValue) endTime = ts;

            if (evType == "user" && root.TryGetProperty("message", out var userMsg))
            {
                // Check if this is a tool result
                bool isToolResult = false;
                if (root.TryGetProperty("toolUseResult", out _) && userMsg.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in uc.EnumerateArray())
                    {
                        if (SafeGetString(block, "type") == "tool_result")
                        {
                            var content = "";
                            if (block.TryGetProperty("content", out var tc))
                            {
                                if (tc.ValueKind == JsonValueKind.String)
                                    content = tc.GetString() ?? "";
                                else if (tc.ValueKind == JsonValueKind.Array)
                                {
                                    // tool_result content can be an array of {type:"text",text:"..."}
                                    List<string> parts = [];
                                    foreach (var part in tc.EnumerateArray())
                                        if (SafeGetString(part, "type") == "text")
                                            parts.Add(part.TryGetProperty("text", out var pt) ? pt.GetString() ?? "" : "");
                                    content = string.Join("\n", parts);
                                }
                                else
                                    content = tc.GetRawText();
                            }
                            var isError = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            var status = isError ? "error" : "success";
                            var toolUseId = SafeGetString(block, "tool_use_id");
                            var syntheticJson = $"{{\"type\":\"tool.result\",\"data\":{{\"toolUseId\":{JsonSerializer.Serialize(toolUseId)},\"result\":{{\"content\":{JsonSerializer.Serialize(content)},\"status\":{JsonSerializer.Serialize(status)}}}}}}}";
                            var synDoc = JsonDocument.Parse(syntheticJson);
                            turns.Add(("tool.result", synDoc.RootElement, ts));
                            isToolResult = true;
                        }
                    }
                }
                if (!isToolResult)
                {
                    // Regular user message
                    var content = "";
                    if (userMsg.TryGetProperty("content", out var mc))
                    {
                        if (mc.ValueKind == JsonValueKind.String)
                            content = mc.GetString() ?? "";
                        else if (mc.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in mc.EnumerateArray())
                            {
                                if (SafeGetString(block, "type") == "text")
                                    content += block.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                            }
                        }
                    }
                    var syntheticJson = $"{{\"type\":\"user.message\",\"data\":{{\"content\":{JsonSerializer.Serialize(content)}}}}}";
                    var synDoc = JsonDocument.Parse(syntheticJson);
                    turns.Add(("user.message", synDoc.RootElement, ts));
                }
            }
            else if (evType == "assistant" && root.TryGetProperty("message", out var asstMsg))
            {
                if (asstMsg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        var blockType = SafeGetString(block, "type");
                        if (blockType == "text")
                        {
                            var text = block.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                var syntheticJson = $"{{\"type\":\"assistant.message\",\"data\":{{\"content\":{JsonSerializer.Serialize(text)}}}}}";
                                var synDoc = JsonDocument.Parse(syntheticJson);
                                turns.Add(("assistant.message", synDoc.RootElement, ts));
                            }
                        }
                        else if (blockType == "tool_use")
                        {
                            var toolName = SafeGetString(block, "name");
                            var toolUseId = SafeGetString(block, "id");
                            var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                            var syntheticJson = $"{{\"type\":\"tool.execution_start\",\"data\":{{\"toolName\":{JsonSerializer.Serialize(toolName)},\"toolUseId\":{JsonSerializer.Serialize(toolUseId)},\"arguments\":{input}}}}}";
                            var synDoc = JsonDocument.Parse(syntheticJson);
                            turns.Add(("tool.execution_start", synDoc.RootElement, ts));
                        }
                        else if (blockType == "thinking")
                        {
                            var text = block.TryGetProperty("thinking", out var th) ? th.GetString() ?? "" : "";
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                var syntheticJson = $"{{\"type\":\"assistant.thinking\",\"data\":{{\"content\":{JsonSerializer.Serialize(text)}}}}}";
                                var synDoc = JsonDocument.Parse(syntheticJson);
                                turns.Add(("assistant.thinking", synDoc.RootElement, ts));
                            }
                        }
                    }
                }
            }
            // Skip progress, system, file-history-snapshot
            // Handle queue-operation events (user messages typed while assistant was working)
            else if (evType == "queue-operation")
            {
                var operation = SafeGetString(root, "operation");
                if (operation == "enqueue" && root.TryGetProperty("message", out var qMsg))
                {
                    var content = "";
                    if (qMsg.TryGetProperty("content", out var qc))
                    {
                        if (qc.ValueKind == JsonValueKind.String)
                            content = qc.GetString() ?? "";
                        else if (qc.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in qc.EnumerateArray())
                                if (SafeGetString(block, "type") == "text")
                                    content += block.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                        }
                    }
                    if (!string.IsNullOrEmpty(content))
                    {
                        var syntheticJson = $"{{\"type\":\"user.message\",\"data\":{{\"content\":{JsonSerializer.Serialize(content)},\"queued\":true}}}}";
                        var synDoc = JsonDocument.Parse(syntheticJson);
                        turns.Add(("user.message", synDoc.RootElement, ts));
                    }
                }
            }
        }

        if (tail.HasValue && tail.Value < turns.Count)
            turns = turns.Skip(turns.Count - tail.Value).ToList();

        return new JsonlData(events, turns, sessionId, branch, version, cwd, startTime, endTime, events.Count);
    }

    public WazaData ParseWazaData(JsonDocument doc)
    {
        var root = doc.RootElement;
        JsonElement[] transcriptItems;
        string taskName = "", taskId = "", status = "", prompt = "", finalOutput = "";
        double durationMs = 0;
        int totalTurns = 0, toolCallCount = 0, tokensIn = 0, tokensOut = 0;
        List<(string name, double score, bool passed, string feedback)> validations = [];

        if (root.ValueKind == JsonValueKind.Array)
        {
            transcriptItems = root.EnumerateArray().ToArray();
        }
        else if (root.TryGetProperty("tasks", out var tasksArr) && tasksArr.ValueKind == JsonValueKind.Array && tasksArr.GetArrayLength() > 0)
        {
            // EvaluationOutcome format (waza -o results.json)
            var task0 = tasksArr[0];
            taskName = SafeGetString(task0, "display_name");
            taskId = SafeGetString(task0, "test_id");
            status = SafeGetString(task0, "status");

            string modelId = "";
            if (root.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
                modelId = SafeGetString(cfg, "model_id");

            double aggregateScore = 0;
            if (root.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object)
            {
                if (summary.TryGetProperty("AggregateScore", out var asc) && asc.ValueKind == JsonValueKind.Number)
                    aggregateScore = asc.GetDouble();
                if (summary.TryGetProperty("DurationMs", out var durMs) && durMs.ValueKind == JsonValueKind.Number)
                    durationMs = durMs.GetDouble();
            }

            string[] toolsUsed = [];
            if (task0.TryGetProperty("runs", out var runsArr) && runsArr.ValueKind == JsonValueKind.Array && runsArr.GetArrayLength() > 0)
            {
                var run0 = runsArr[0];
                transcriptItems = run0.TryGetProperty("transcript", out var tr2) && tr2.ValueKind == JsonValueKind.Array
                    ? tr2.EnumerateArray().ToArray() : [];
                finalOutput = SafeGetString(run0, "final_output");
                if (run0.TryGetProperty("duration_ms", out var rdm) && rdm.ValueKind == JsonValueKind.Number)
                    durationMs = rdm.GetDouble();
                if (run0.TryGetProperty("session_digest", out var sd) && sd.ValueKind == JsonValueKind.Object)
                {
                    if (sd.TryGetProperty("total_turns", out var tt2)) totalTurns = tt2.TryGetInt32(out var vt) ? vt : 0;
                    if (sd.TryGetProperty("tool_call_count", out var tc2)) toolCallCount = tc2.TryGetInt32(out var vc) ? vc : 0;
                    if (sd.TryGetProperty("tokens_in", out var ti2)) tokensIn = ti2.TryGetInt32(out var vi) ? vi : 0;
                    if (sd.TryGetProperty("tokens_out", out var to3)) tokensOut = to3.TryGetInt32(out var vo) ? vo : 0;
                    if (sd.TryGetProperty("tools_used", out var tu) && tu.ValueKind == JsonValueKind.Array)
                        toolsUsed = tu.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray();
                }
                if (run0.TryGetProperty("validations", out var rvals) && rvals.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in rvals.EnumerateObject())
                    {
                        double score = 0; bool passed = false; string feedback = "";
                        if (prop.Value.TryGetProperty("score", out var sc2) && sc2.ValueKind == JsonValueKind.Number) score = sc2.GetDouble();
                        if (prop.Value.TryGetProperty("passed", out var pa2)) passed = pa2.ValueKind == JsonValueKind.True;
                        if (prop.Value.TryGetProperty("feedback", out var fb2)) feedback = SafeGetString(prop.Value, "feedback");
                        validations.Add((prop.Name, score, passed, feedback));
                    }
                }
            }
            else
            {
                transcriptItems = [];
            }

            if (tail.HasValue && tail.Value < transcriptItems.Length)
                transcriptItems = transcriptItems.Skip(transcriptItems.Length - tail.Value).ToArray();

            return new WazaData(transcriptItems, taskName, taskId, status, prompt, finalOutput,
                durationMs, totalTurns, toolCallCount, tokensIn, tokensOut, validations,
                modelId, aggregateScore, toolsUsed);
        }
        else
        {
            taskName = SafeGetString(root, "task_name");
            taskId = SafeGetString(root, "task_id");
            status = SafeGetString(root, "status");
            prompt = SafeGetString(root, "prompt");
            finalOutput = SafeGetString(root, "final_output");
            if (root.TryGetProperty("duration_ms", out var dm) && dm.ValueKind == JsonValueKind.Number)
                durationMs = dm.GetDouble();
            if (root.TryGetProperty("session", out var sess) && sess.ValueKind == JsonValueKind.Object)
            {
                if (sess.TryGetProperty("total_turns", out var tt)) totalTurns = tt.TryGetInt32(out var v) ? v : 0;
                if (sess.TryGetProperty("tool_call_count", out var tc)) toolCallCount = tc.TryGetInt32(out var v2) ? v2 : 0;
                if (sess.TryGetProperty("tokens_in", out var ti)) tokensIn = ti.TryGetInt32(out var v3) ? v3 : 0;
                if (sess.TryGetProperty("tokens_out", out var to2)) tokensOut = to2.TryGetInt32(out var v4) ? v4 : 0;
            }
            if (root.TryGetProperty("validations", out var vals) && vals.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in vals.EnumerateObject())
                {
                    double score = 0; bool passed = false; string feedback = "";
                    if (prop.Value.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number) score = sc.GetDouble();
                    if (prop.Value.TryGetProperty("passed", out var pa)) passed = pa.ValueKind == JsonValueKind.True;
                    if (prop.Value.TryGetProperty("feedback", out var fb)) feedback = SafeGetString(prop.Value, "feedback");
                    validations.Add((prop.Name, score, passed, feedback));
                }
            }
            transcriptItems = root.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.Array
                ? tr.EnumerateArray().ToArray() : [];
        }

        if (tail.HasValue && tail.Value < transcriptItems.Length)
            transcriptItems = transcriptItems.Skip(transcriptItems.Length - tail.Value).ToArray();

        return new WazaData(transcriptItems, taskName, taskId, status, prompt, finalOutput,
            durationMs, totalTurns, toolCallCount, tokensIn, tokensOut, validations);
    }
}
