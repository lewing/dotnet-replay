#:property ToolCommandName=replay
#:property PackageId=dotnet-replay
#:property Version=0.2.0
#:property Authors=Larry Ewing
#:property Description=Interactive transcript viewer for Copilot CLI sessions and waza evaluations
#:property PackageLicenseExpression=MIT
#:property RepositoryUrl=https://github.com/lewing/dotnet-replay
#:property PackageTags=copilot;transcript;viewer;waza;evaluation;cli
#:property PublishAot=false

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

Console.OutputEncoding = Encoding.UTF8;

// --- CLI argument parsing ---
var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
string? filePath = null;
int? tail = null;
bool expandTools = false;
bool full = false;
string? filterType = null;
bool noColor = false;
bool streamMode = false;
bool noFollow = false;

for (int i = 0; i < cliArgs.Length; i++)
{
    switch (cliArgs[i])
    {
        case "--help":
        case "-h":
            PrintHelp();
            return;
        case "--tail":
            if (i + 1 < cliArgs.Length && int.TryParse(cliArgs[++i], out int t)) tail = t;
            else { Console.Error.WriteLine("Error: --tail requires a numeric argument"); return; }
            break;
        case "--expand-tools":
            expandTools = true;
            break;
        case "--full":
            full = true;
            break;
        case "--filter":
            if (i + 1 < cliArgs.Length) filterType = cliArgs[++i].ToLowerInvariant();
            else { Console.Error.WriteLine("Error: --filter requires an argument"); return; }
            break;
        case "--no-color":
            noColor = true;
            break;
        case "--stream":
            streamMode = true;
            break;
        case "--no-follow":
            noFollow = true;
            break;
        default:
            if (cliArgs[i].StartsWith("-")) { Console.Error.WriteLine($"Unknown option: {cliArgs[i]}"); PrintHelp(); return; }
            filePath = cliArgs[i];
            break;
    }
}

// Auto-select stream mode when output is redirected (piped to file or another process)
if (Console.IsOutputRedirected) streamMode = true;

if (filePath is null) { Console.Error.WriteLine("Error: No file specified"); PrintHelp(); return; }
if (!File.Exists(filePath)) { Console.Error.WriteLine($"Error: File not found: {filePath}"); return; }

// --- Color helpers ---
string Blue(string s) => noColor ? s : $"\x1b[34m{s}\x1b[0m";
string Green(string s) => noColor ? s : $"\x1b[32m{s}\x1b[0m";
string Yellow(string s) => noColor ? s : $"\x1b[33m{s}\x1b[0m";
string Red(string s) => noColor ? s : $"\x1b[31m{s}\x1b[0m";
string Dim(string s) => noColor ? s : $"\x1b[2m{s}\x1b[0m";
string Bold(string s) => noColor ? s : $"\x1b[1m{s}\x1b[0m";
string Cyan(string s) => noColor ? s : $"\x1b[36m{s}\x1b[0m";
string Separator()
{
    int width = 80;
    try
    {
        width = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
    }
    catch (IOException)
    {
        // No console available (redirected), use default
    }
    return Dim(new string('─', Math.Min(width, 120)));
}

string PadVisible(string s, int totalWidth)
{
    // Strip ANSI codes to measure visible length, then pad with spaces
    var visible = System.Text.RegularExpressions.Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
    int padding = totalWidth - visible.Length;
    return padding > 0 ? s + new string(' ', padding) : s;
}

string FormatRelativeTime(TimeSpan ts)
{
    if (ts.TotalSeconds < 0.1) return "+0.0s";
    if (ts.TotalMinutes < 1) return $"+{ts.TotalSeconds:F1}s";
    if (ts.TotalHours < 1) return $"+{(int)ts.TotalMinutes}m {ts.Seconds}s";
    return $"+{(int)ts.TotalHours}h {ts.Minutes}m";
}

string Truncate(string s, int max)
{
    if (full || s.Length <= max) return s;
    return s[..max] + $"… [{s.Length - max} more chars]";
}

List<string> FormatJsonProperties(JsonElement obj, string linePrefix, int maxValueLen)
{
    var lines = new List<string>();
    if (obj.ValueKind == JsonValueKind.Object)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var val = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? ""
                : prop.Value.GetRawText();
            var truncated = Truncate(val, maxValueLen);
            foreach (var segment in truncated.Split('\n'))
                lines.Add($"{linePrefix}{prop.Name}: {segment}");
        }
    }
    else if (obj.ValueKind == JsonValueKind.String)
    {
        var val = obj.GetString() ?? "";
        var truncated = Truncate(val, maxValueLen);
        foreach (var segment in truncated.Split('\n'))
            lines.Add($"{linePrefix}{segment}");
    }
    else
    {
        lines.Add($"{linePrefix}{Truncate(obj.GetRawText(), maxValueLen)}");
    }
    return lines;
}

string ExtractContentString(JsonElement el)
{
    if (el.ValueKind == JsonValueKind.String)
        return el.GetString() ?? "";
    if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
        return c.GetString() ?? "";
    if (el.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? "";
            if (item.ValueKind == JsonValueKind.String)
                return item.GetString() ?? "";
        }
    }
    return el.GetRawText();
}

string SafeGetString(JsonElement el, string prop)
{
    if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
        return v.GetString() ?? "";
    return "";
}

// --- Format detection ---
var firstLine = "";
using (var reader = new StreamReader(filePath))
{
    firstLine = reader.ReadLine() ?? "";
}

bool isJsonl = filePath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
    || (!filePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
        && firstLine.TrimStart().StartsWith("{") && !firstLine.TrimStart().StartsWith("["));

bool isWaza = false;
JsonDocument? wazaDoc = null;

if (!isJsonl)
{
    try
    {
        var jsonText = File.ReadAllText(filePath);
        wazaDoc = JsonDocument.Parse(jsonText);
        var root = wazaDoc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("transcript", out _))
            isWaza = true;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tasks", out var tasksCheck) && tasksCheck.ValueKind == JsonValueKind.Array)
            isWaza = true;
        else if (root.ValueKind == JsonValueKind.Array)
            isWaza = true;
        else
        {
            Console.Error.WriteLine("Error: Could not determine file format. Expected .jsonl (Copilot CLI) or JSON with 'transcript' field (Waza eval).");
            return;
        }
    }
    catch (JsonException)
    {
        Console.Error.WriteLine("Error: Could not determine file format. File is not valid JSON or JSONL.");
        return;
    }
}

if (streamMode)
{
    // Stream mode: original dump-everything behavior
    if (isJsonl) StreamEventsJsonl(filePath);
    else if (isWaza) StreamWazaTranscript(wazaDoc!);
}
else
{
    // Interactive pager mode (default)
    List<string> headerLines;
    List<string> contentLines;
    // We need the parsed data for re-rendering with different tool/filter settings
    if (isJsonl)
    {
        var parsed = ParseJsonlData(filePath);
        if (parsed is null) return;
        headerLines = RenderJsonlHeaderLines(parsed);
        contentLines = RenderJsonlContentLines(parsed, filterType, expandTools);
        RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: true, noFollow: noFollow);
    }
    else if (isWaza)
    {
        var parsed = ParseWazaData(wazaDoc!);
        headerLines = RenderWazaHeaderLines(parsed);
        contentLines = RenderWazaContentLines(parsed, filterType, expandTools);
        RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: false, noFollow: true);
    }
}

// ========== JSONL Parser ==========
JsonlData? ParseJsonlData(string path)
{
    var events = new List<JsonDocument>();
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try { events.Add(JsonDocument.Parse(line)); }
        catch { /* skip malformed lines */ }
    }
    if (events.Count == 0) { Console.Error.WriteLine(Bold("No events found")); return null; }

    string sessionId = "", branch = "", copilotVersion = "", cwd = "";
    DateTimeOffset? startTime = null, endTime = null;

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

    var turns = new List<(string type, JsonElement root, DateTimeOffset? ts)>();
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

// ========== Waza Parser ==========
WazaData ParseWazaData(JsonDocument doc)
{
    var root = doc.RootElement;
    JsonElement[] transcriptItems;
    string taskName = "", taskId = "", status = "", prompt = "", finalOutput = "";
    double durationMs = 0;
    int totalTurns = 0, toolCallCount = 0, tokensIn = 0, tokensOut = 0;
    var validations = new List<(string name, double score, bool passed, string feedback)>();

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

// ========== Info bar builders (compact 1-line header) ==========
string BuildJsonlInfoBar(JsonlData d)
{
    var parts = new List<string>();
    if (d.SessionId != "") parts.Add($"session {d.SessionId[..Math.Min(d.SessionId.Length, 8)]}");
    if (d.CopilotVersion != "") parts.Add(d.CopilotVersion);
    parts.Add($"{d.EventCount} events");
    return $"[{string.Join(" | ", parts)}]";
}

string BuildWazaInfoBar(WazaData d)
{
    var parts = new List<string>();
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

// ========== Full header builders (for 'i' overlay) ==========
List<string> RenderJsonlHeaderLines(JsonlData d)
{
    var lines = new List<string>();
    lines.Add("");
    lines.Add(Bold(Cyan("╭─────────────────────────────────────────────────────╮")));
    lines.Add(Bold(Cyan("│")) + Bold("  📋 Copilot CLI Session Log                         ") + Bold(Cyan("│")));
    lines.Add(Bold(Cyan("├─────────────────────────────────────────────────────┤")));
    if (d.SessionId != "") lines.Add(Bold(Cyan("│")) + PadVisible($"  Session:  {Dim(d.SessionId)}", 53) + Bold(Cyan("│")));
    if (d.StartTime.HasValue) lines.Add(Bold(Cyan("│")) + PadVisible($"  Started:  {Dim(d.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"))}", 53) + Bold(Cyan("│")));
    if (d.Branch != "") lines.Add(Bold(Cyan("│")) + PadVisible($"  Branch:   {Dim(d.Branch)}", 53) + Bold(Cyan("│")));
    if (d.CopilotVersion != "") lines.Add(Bold(Cyan("│")) + PadVisible($"  Version:  {Dim(d.CopilotVersion)}", 53) + Bold(Cyan("│")));
    var duration = (d.StartTime.HasValue && d.EndTime.HasValue) ? d.EndTime.Value - d.StartTime.Value : TimeSpan.Zero;
    lines.Add(Bold(Cyan("│")) + PadVisible($"  Events:   {Dim(d.EventCount.ToString())}", 53) + Bold(Cyan("│")));
    lines.Add(Bold(Cyan("│")) + PadVisible($"  Duration: {Dim(FormatRelativeTime(duration))}", 53) + Bold(Cyan("│")));
    lines.Add(Bold(Cyan("╰─────────────────────────────────────────────────────╯")));
    lines.Add("");
    return lines;
}

List<string> RenderWazaHeaderLines(WazaData d)
{
    var lines = new List<string>();
    double avgScore = d.Validations.Count > 0 ? d.Validations.Average(v => v.score) : 0;

    lines.Add("");
    lines.Add(Bold(Cyan("╭─────────────────────────────────────────────────────╮")));
    lines.Add(Bold(Cyan("│")) + Bold("  🧪 Waza Eval Transcript                             ") + Bold(Cyan("│")));
    lines.Add(Bold(Cyan("├─────────────────────────────────────────────────────┤")));
    if (d.TaskName != "") lines.Add(Bold(Cyan("│")) + PadVisible($"  Task:     {Bold(d.TaskName)}", 53) + Bold(Cyan("│")));
    if (d.TaskId != "") lines.Add(Bold(Cyan("│")) + PadVisible($"  ID:       {Dim(d.TaskId)}", 53) + Bold(Cyan("│")));
    if (d.Status != "")
    {
        var statusStr = d.Status.ToLowerInvariant() switch
        {
            "passed" => Green("✅ PASS"),
            "failed" => Red("❌ FAIL"),
            _ => Red($"⚠ {d.Status.ToUpperInvariant()}")
        };
        lines.Add(Bold(Cyan("│")) + PadVisible($"  Status:   {statusStr}", 53) + Bold(Cyan("│")));
    }
    if (d.Validations.Count > 0)
    {
        Func<string, string> scoreFn = avgScore >= 0.7 ? Green : Red;
        lines.Add(Bold(Cyan("│")) + PadVisible($"  Score:    {scoreFn($"{avgScore:P0}")}", 53) + Bold(Cyan("│")));
    }
    if (d.DurationMs > 0)
        lines.Add(Bold(Cyan("│")) + PadVisible($"  Duration: {Dim(FormatRelativeTime(TimeSpan.FromMilliseconds(d.DurationMs)))}", 53) + Bold(Cyan("│")));
    if (d.ToolCallCount > 0)
        lines.Add(Bold(Cyan("│")) + PadVisible($"  Tools:    {Dim($"{d.ToolCallCount} calls")}", 53) + Bold(Cyan("│")));
    if (d.TokensIn > 0 || d.TokensOut > 0)
        lines.Add(Bold(Cyan("│")) + PadVisible($"  Tokens:   {Dim($"in={d.TokensIn}, out={d.TokensOut}")}", 53) + Bold(Cyan("│")));
    lines.Add(Bold(Cyan("╰─────────────────────────────────────────────────────╯")));

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
List<string> RenderJsonlContentLines(JsonlData d, string? filter, bool expandTool)
{
    var lines = new List<string>();
    var filtered = d.Turns.AsEnumerable();

    if (filter is not null)
    {
        filtered = filtered.Where(t => filter switch
        {
            "user" => t.type == "user.message",
            "assistant" => t.type == "assistant.message",
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
                lines.Add(margin + Blue("┃ USER"));
                foreach (var line in content.Split('\n'))
                    lines.Add(margin + Blue($"┃ {line}"));
                break;
            }
            case "assistant.message":
            {
                lines.Add(Separator());
                var content = SafeGetString(data, "content");
                lines.Add(margin + Green("┃ ASSISTANT"));
                if (!string.IsNullOrEmpty(content))
                    foreach (var line in content.Split('\n'))
                        lines.Add(margin + Green($"┃ {line}"));
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
                break;
            }
            case "tool.execution_start":
            {
                var toolName = SafeGetString(data, "toolName");
                lines.Add(margin + Yellow($"┃ TOOL: {toolName}"));
                if (expandTool && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("arguments", out var toolArgs))
                {
                    lines.Add(margin + Dim("┃   Args:"));
                    foreach (var pl in FormatJsonProperties(toolArgs, "┃     ", 500))
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
                var colorFn = isError ? (Func<string, string>)Red : Dim;
                lines.Add(margin + colorFn(isError ? "┃ ❌ ERROR" : "┃ ✅ Result"));
                if (expandTool && !string.IsNullOrEmpty(resultContent))
                {
                    if (resultContent.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                        lines.Add(margin + colorFn($"┃   [binary content, {resultContent.Length} bytes]"));
                    else
                    {
                        var truncated = Truncate(resultContent, 500);
                        foreach (var line in truncated.Split('\n').Take(full ? int.MaxValue : 20))
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

List<string> RenderWazaContentLines(WazaData d, string? filter, bool expandTool)
{
    var lines = new List<string>();
    if (d.TranscriptItems.Length == 0) { lines.Add(Dim("  No events found")); return lines; }

    int turnIndex = 0;
    // Track message index to alternate user/assistant for "message" type events
    int messageIndex = 0;
    foreach (var item in d.TranscriptItems)
    {
        var itemType = SafeGetString(item, "type").ToLowerInvariant();
        var content = SafeGetString(item, "content");
        if (string.IsNullOrEmpty(content)) content = SafeGetString(item, "message");
        var toolName = SafeGetString(item, "tool_name");

        // Classify "message" type: first message is user, tool-adjacent messages are assistant
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

        turnIndex++;
        var margin = Dim($"  {turnIndex,5}  ");
        lines.Add(Separator());

        if (isUserMessage)
        {
            lines.Add(margin + Blue("┃ USER"));
            foreach (var line in content.Split('\n'))
                lines.Add(margin + Blue($"┃ {line}"));
        }
        else if (isAssistantMessage)
        {
            lines.Add(margin + Green("┃ ASSISTANT"));
            foreach (var line in content.Split('\n'))
                lines.Add(margin + Green($"┃ {line}"));
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
                        var resLines = truncated.Split('\n').Take(full ? int.MaxValue : 20).ToArray();
                        if (resLines.Length > 0)
                            lines.Add(margin + Dim($"┃   Result: {resLines[0]}"));
                        foreach (var line in resLines.Skip(1))
                            lines.Add(margin + Dim($"┃   {line}"));
                    }
                }
                if (!string.IsNullOrEmpty(content))
                    lines.Add(margin + Dim($"┃   {Truncate(content, 500)}"));
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

// ========== Interactive Pager ==========
void RunInteractivePager<T>(List<string> headerLines, List<string> contentLines, T parsedData, bool isJsonlFormat, bool noFollow)
{
    int scrollOffset = 0;
    string? currentFilter = filterType;
    bool currentExpandTools = expandTools;
    string? searchPattern = null;
    List<int> searchMatches = new();
    int searchMatchIndex = -1;
    bool inSearchMode = false;
    var searchBuffer = new StringBuilder();
    bool showInfoOverlay = false;

    // Follow mode state
    bool following = isJsonlFormat && !noFollow;
    bool userAtBottom = true;
    long lastFileOffset = 0;
    int fileChangedFlag = 0; // 0=no change, 1=changed; use Interlocked for thread safety
    DateTime lastReadTime = DateTime.MinValue;
    FileSystemWatcher? watcher = null;

    // Track terminal dimensions for resize detection
    int lastWidth = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
    int lastHeight = Console.WindowHeight > 0 ? Console.WindowHeight : 24;
    bool needsFullClear = true; // first render does a full clear

    // Build compact info bar
    string infoBar;
    if (isJsonlFormat && parsedData is JsonlData jData)
        infoBar = BuildJsonlInfoBar(jData) + (following ? " ↓ FOLLOWING" : "");
    else if (parsedData is WazaData wData)
        infoBar = BuildWazaInfoBar(wData);
    else
        infoBar = $"[{Path.GetFileName(filePath)}]";

    string[] filterCycle = ["all", "user", "assistant", "tool", "error"];
    int filterIndex = currentFilter is null ? 0 : Array.IndexOf(filterCycle, currentFilter);
    if (filterIndex < 0) filterIndex = 0;

    string StripAnsi(string s) => Regex.Replace(s, @"\x1b\[[0-9;]*m", "");
    string HighlightLine(string s) => noColor ? s : $"\x1b[46m\x1b[30m{StripAnsi(s)}\x1b[0m";

    void RebuildContent()
    {
        var filter = filterIndex == 0 ? null : filterCycle[filterIndex];
        if (isJsonlFormat && parsedData is JsonlData jd)
            contentLines = RenderJsonlContentLines(jd, filter, currentExpandTools);
        else if (parsedData is WazaData wd)
            contentLines = RenderWazaContentLines(wd, filter, currentExpandTools);
        if (searchPattern is not null)
            RebuildSearchMatches();
        ClampScroll();
    }

    void RebuildSearchMatches()
    {
        searchMatches.Clear();
        searchMatchIndex = -1;
        if (searchPattern is null || searchPattern.Length == 0) return;
        for (int i = 0; i < contentLines.Count; i++)
        {
            if (StripAnsi(contentLines[i]).Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                searchMatches.Add(i);
        }
    }

    void ClampScroll()
    {
        int maxOffset = Math.Max(0, contentLines.Count - ViewportHeight());
        scrollOffset = Math.Max(0, Math.Min(scrollOffset, maxOffset));
    }

    int ViewportHeight()
    {
        int h = Console.WindowHeight > 0 ? Console.WindowHeight : 24;
        // 1 info bar line + 1 status bar line = 2 chrome lines
        return Math.Max(1, h - 2);
    }

    void WriteLine(int row, string text, int width)
    {
        Console.SetCursorPosition(0, row);
        // Truncate visible content to terminal width and pad to overwrite stale content
        var visible = StripAnsi(text);
        if (visible.Length >= width)
        {
            // Need to truncate — but preserve ANSI codes up to the visible width
            Console.Write(text);
            // Ensure we don't leave stale chars beyond what we wrote
        }
        else
        {
            Console.Write(text + new string(' ', width - visible.Length));
        }
    }

    void Render()
    {
        int w = Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        int h = Console.WindowHeight > 0 ? Console.WindowHeight : 24;

        // Detect terminal resize
        if (w != lastWidth || h != lastHeight)
        {
            needsFullClear = true;
            lastWidth = w;
            lastHeight = h;
        }

        if (needsFullClear)
        {
            Console.Clear();
            needsFullClear = false;
        }

        Console.CursorVisible = false;

        int row = 0;

        // Row 0: Compact info bar (inverted)
        string topBar = " " + infoBar;
        var visibleTop = StripAnsi(topBar);
        if (visibleTop.Length < w)
            topBar += new string(' ', w - visibleTop.Length);
        Console.SetCursorPosition(0, row);
        Console.Write(noColor ? topBar : $"\x1b[7m{topBar}\x1b[0m");
        row++;

        // Content viewport
        int vpHeight = ViewportHeight();
        int end = Math.Min(scrollOffset + vpHeight, contentLines.Count);

        // If info overlay is shown, render headerLines over the viewport
        if (showInfoOverlay)
        {
            int overlayLines = Math.Min(headerLines.Count, vpHeight);
            for (int i = 0; i < overlayLines; i++)
            {
                WriteLine(row, headerLines[i], w);
                row++;
            }
            // Fill rest of viewport
            for (int i = overlayLines; i < vpHeight; i++)
            {
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', w));
                row++;
            }
        }
        else
        {
            for (int i = scrollOffset; i < end; i++)
            {
                var line = contentLines[i];
                bool isMatch = searchPattern is not null && searchMatches.Contains(i);
                WriteLine(row, isMatch ? HighlightLine(line) : line, w);
                row++;
            }

            // Fill remaining viewport with blank lines
            int rendered = end - scrollOffset;
            for (int i = rendered; i < vpHeight; i++)
            {
                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', w));
                row++;
            }
        }

        // Status bar (bottom line)
        var statusFilter = filterIndex == 0 ? "all" : filterCycle[filterIndex];
        int currentLine = contentLines.Count == 0 ? 0 : scrollOffset + 1;
        string statusBar;
        if (showInfoOverlay)
        {
            statusBar = " Press i or any key to dismiss";
        }
        else if (inSearchMode)
        {
            statusBar = $" Search: {searchBuffer}_";
        }
        else if (searchPattern is not null && searchMatches.Count > 0)
        {
            statusBar = $" Search: \"{searchPattern}\" ({searchMatchIndex + 1}/{searchMatches.Count}) | n/N next/prev | Esc clear";
        }
        else
        {
            var followIndicator = following ? (userAtBottom ? " LIVE" : " [new content ↓]") : "";
            statusBar = $" Line {currentLine}/{contentLines.Count} | Filter: {statusFilter}{followIndicator} | \u2191\u2193 j/k scroll | Space page | / search | q quit";
        }
        var visibleStatus = StripAnsi(statusBar);
        if (visibleStatus.Length < w)
            statusBar += new string(' ', w - visibleStatus.Length);
        Console.SetCursorPosition(0, row);
        Console.Write(noColor ? statusBar : $"\x1b[7m{statusBar}\x1b[0m");

        Console.CursorVisible = true;
    }

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.CursorVisible = true;
        Console.Clear();
        Environment.Exit(0);
    };

    Console.CursorVisible = false;
    try
    {
        // Set up FileSystemWatcher for follow mode
        if (following && filePath is not null)
        {
            lastFileOffset = new FileInfo(filePath).Length;
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            var name = Path.GetFileName(filePath);
            watcher = new FileSystemWatcher(dir, name);
            watcher.Changed += (_, _) => Interlocked.Exchange(ref fileChangedFlag, 1);
            watcher.EnableRaisingEvents = true;
        }

        Render();

        while (true)
        {
            // Check for file changes in follow mode
            if (following && Interlocked.CompareExchange(ref fileChangedFlag, 0, 1) == 1 && filePath is not null)
            {
                var now = DateTime.UtcNow;
                if ((now - lastReadTime).TotalMilliseconds >= 100)
                {
                    lastReadTime = now;
                    try
                    {
                        var fi = new FileInfo(filePath);
                        if (fi.Length > lastFileOffset && parsedData is JsonlData jdFollow)
                        {
                            var newLines = new List<string>();
                            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                fs.Seek(lastFileOffset, SeekOrigin.Begin);
                                using var sr = new StreamReader(fs, Encoding.UTF8);
                                string? line;
                                while ((line = sr.ReadLine()) != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(line))
                                        newLines.Add(line);
                                }
                                lastFileOffset = fs.Position;
                            }

                            if (newLines.Count > 0)
                            {
                                bool wasAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                                foreach (var rawLine in newLines)
                                {
                                    try
                                    {
                                        var doc = JsonDocument.Parse(rawLine);
                                        jdFollow.Events.Add(doc);
                                        var root = doc.RootElement;
                                        var evType = SafeGetString(root, "type");
                                        var tsStr = SafeGetString(root, "timestamp");
                                        DateTimeOffset? ts = null;
                                        if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                                            ts = dto;
                                        if (evType is "user.message" or "assistant.message" or "tool.execution_start" or "tool.result")
                                            jdFollow.Turns.Add((evType, root, ts));
                                        jdFollow.EventCount = jdFollow.Events.Count;
                                    }
                                    catch { /* skip malformed lines */ }
                                }
                                RebuildContent();
                                infoBar = BuildJsonlInfoBar(jdFollow) + " ↓ FOLLOWING";
                                if (wasAtBottom && userAtBottom)
                                {
                                    scrollOffset = Math.Max(0, contentLines.Count - ViewportHeight());
                                }
                                Render();
                            }
                        }
                    }
                    catch { /* ignore read errors, will retry on next change */ }
                }
            }

            if (!Console.KeyAvailable)
            {
                Thread.Sleep(50);
                continue;
            }
            var key = Console.ReadKey(true);

            if (inSearchMode)
            {
                if (key.Key == ConsoleKey.Escape)
                {
                    inSearchMode = false;
                    searchBuffer.Clear();
                    Render();
                    continue;
                }
                if (key.Key == ConsoleKey.Enter)
                {
                    inSearchMode = false;
                    searchPattern = searchBuffer.ToString();
                    searchBuffer.Clear();
                    if (searchPattern.Length == 0) { searchPattern = null; searchMatches.Clear(); searchMatchIndex = -1; }
                    else
                    {
                        RebuildSearchMatches();
                        if (searchMatches.Count > 0)
                        {
                            searchMatchIndex = 0;
                            scrollOffset = Math.Max(0, searchMatches[0] - ViewportHeight() / 3);
                            ClampScroll();
                        }
                    }
                    Render();
                    continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (searchBuffer.Length > 0) searchBuffer.Remove(searchBuffer.Length - 1, 1);
                    Render();
                    continue;
                }
                if (key.KeyChar >= 32)
                {
                    searchBuffer.Append(key.KeyChar);
                    Render();
                    continue;
                }
                continue;
            }

            // Dismiss info overlay on any key
            if (showInfoOverlay)
            {
                showInfoOverlay = false;
                Render();
                continue;
            }

            // Normal mode key handling
            switch (key.Key)
            {
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    return;

                case ConsoleKey.UpArrow:
                    scrollOffset = Math.Max(0, scrollOffset - 1);
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.DownArrow:
                    scrollOffset++;
                    ClampScroll();
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.LeftArrow:
                case ConsoleKey.PageUp:
                    scrollOffset = Math.Max(0, scrollOffset - ViewportHeight());
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.RightArrow:
                case ConsoleKey.PageDown:
                case ConsoleKey.Spacebar:
                    scrollOffset += ViewportHeight();
                    ClampScroll();
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.Home:
                    scrollOffset = 0;
                    userAtBottom = contentLines.Count <= ViewportHeight();
                    Render();
                    break;

                case ConsoleKey.End:
                    scrollOffset = Math.Max(0, contentLines.Count - ViewportHeight());
                    userAtBottom = true;
                    Render();
                    break;

                default:
                    switch (key.KeyChar)
                    {
                        case 'k':
                            scrollOffset = Math.Max(0, scrollOffset - 1);
                            userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                            Render();
                            break;
                        case 'j':
                            scrollOffset++;
                            ClampScroll();
                            userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                            Render();
                            break;
                        case 'h':
                            scrollOffset = Math.Max(0, scrollOffset - ViewportHeight());
                            userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                            Render();
                            break;
                        case 'l':
                            scrollOffset += ViewportHeight();
                            ClampScroll();
                            userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                            Render();
                            break;
                        case 'g':
                            scrollOffset = 0;
                            userAtBottom = contentLines.Count <= ViewportHeight();
                            Render();
                            break;
                        case 'G':
                            scrollOffset = Math.Max(0, contentLines.Count - ViewportHeight());
                            userAtBottom = true;
                            Render();
                            break;
                        case 't':
                            currentExpandTools = !currentExpandTools;
                            RebuildContent();
                            Render();
                            break;
                        case 'f':
                            filterIndex = (filterIndex + 1) % filterCycle.Length;
                            RebuildContent();
                            Render();
                            break;
                        case '/':
                            inSearchMode = true;
                            searchBuffer.Clear();
                            Render();
                            break;
                        case 'i':
                            showInfoOverlay = !showInfoOverlay;
                            Render();
                            break;
                        case 'n':
                            if (searchMatches.Count > 0)
                            {
                                searchMatchIndex = (searchMatchIndex + 1) % searchMatches.Count;
                                scrollOffset = Math.Max(0, searchMatches[searchMatchIndex] - ViewportHeight() / 3);
                                ClampScroll();
                                Render();
                            }
                            break;
                        case 'N':
                            if (searchMatches.Count > 0)
                            {
                                searchMatchIndex = (searchMatchIndex - 1 + searchMatches.Count) % searchMatches.Count;
                                scrollOffset = Math.Max(0, searchMatches[searchMatchIndex] - ViewportHeight() / 3);
                                ClampScroll();
                                Render();
                            }
                            break;
                    }
                    break;
            }
        }
    }
    finally
    {
        watcher?.Dispose();
        Console.CursorVisible = true;
        Console.Clear();
    }
}

// ========== Stream Mode (original dump behavior) ==========
void StreamEventsJsonl(string path)
{
    var parsed = ParseJsonlData(path);
    if (parsed is null) return;
    var d = parsed;

    // Header card
    foreach (var line in RenderJsonlHeaderLines(d))
        Console.WriteLine(line);

    // Content
    foreach (var line in RenderJsonlContentLines(d, filterType, expandTools))
        Console.WriteLine(line);
}

void StreamWazaTranscript(JsonDocument doc)
{
    var d = ParseWazaData(doc);
    foreach (var line in RenderWazaHeaderLines(d))
        Console.WriteLine(line);
    foreach (var line in RenderWazaContentLines(d, filterType, expandTools))
        Console.WriteLine(line);
}

void PrintHelp()
{
    Console.WriteLine(@"
Usage: transcript-viewer <file> [options]

  <file>              Path to .jsonl or .json transcript file

Options:
  --tail <N>          Show only the last N conversation turns
  --expand-tools      Show tool call arguments and results
  --full              Don't truncate tool output (use with --expand-tools)
  --filter <type>     Filter by event type: user, assistant, tool, error
  --no-color          Disable ANSI colors
  --stream            Use streaming output (non-interactive, original behavior)
  --no-follow         Disable auto-follow for JSONL files
  --help              Show this help

Interactive mode (default):
  ↑/k ↓/j             Scroll up/down one line
  ←/h/PgUp →/l/PgDn   Page up/down
  Space                Page down
  g/Home  G/End        Jump to start/end
  t                    Toggle tool expansion
  f                    Cycle filter: all → user → assistant → tool → error
  i                    Toggle full session info overlay
  /                    Search (Enter to execute, Esc to cancel)
  n/N                  Next/previous search match
  q/Escape             Quit

JSONL files auto-follow by default (like tail -f). Use --no-follow to disable.
When output is piped (redirected), stream mode is used automatically.
");
}

// ========== Parsed data structures (must follow top-level statements) ==========
record JsonlData(
    List<JsonDocument> Events,
    List<(string type, JsonElement root, DateTimeOffset? ts)> Turns,
    string SessionId, string Branch, string CopilotVersion, string Cwd,
    DateTimeOffset? StartTime, DateTimeOffset? EndTime, int EventCount)
{
    public int EventCount { get; set; } = EventCount;
}

record WazaData(
    JsonElement[] TranscriptItems,
    string TaskName, string TaskId, string Status, string Prompt, string FinalOutput,
    double DurationMs, int TotalTurns, int ToolCallCount, int TokensIn, int TokensOut,
    List<(string name, double score, bool passed, string feedback)> Validations,
    string ModelId = "", double AggregateScore = 0, string[] ToolsUsed = null!);
