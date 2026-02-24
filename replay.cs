using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using static TextUtils;
using static EvalProcessor;

Console.OutputEncoding = Encoding.UTF8;
MarkdownRenderer? mdRenderer = null; // initialized after noColor is parsed
ColorHelper? colors = null;

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
bool jsonMode = false;
bool summaryMode = false;
ContentRenderer? cr = null;

string? dbPath = null;
for (int i = 0; i < cliArgs.Length; i++)
{
    // Skip general parsing when stats command is used — it has its own parser
    if (i == 0 && cliArgs[0] == "stats") break;
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
        case "--json":
            jsonMode = true;
            break;
        case "--summary":
            summaryMode = true;
            break;
        case "--db":
            if (i + 1 < cliArgs.Length) dbPath = cliArgs[++i];
            else { Console.Error.WriteLine("Error: --db requires a file path"); return; }
            break;
        default:
            if (cliArgs[i].StartsWith("-")) { Console.Error.WriteLine($"Unknown option: {cliArgs[i]}"); PrintHelp(); return; }
            // Treat .db files as dbPath, not filePath
            if (cliArgs[i].EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                dbPath = cliArgs[i];
            else
                filePath = cliArgs[i];
            break;
    }
}

// Auto-select stream mode when output is redirected (piped to file or another process)
if (Console.IsOutputRedirected)
{
    streamMode = true;
    noColor = true; // Markup can't render to redirected output
}

// JSON mode implies stream mode
if (jsonMode || summaryMode)
{
    streamMode = true;
    noColor = true;
}

mdRenderer = new MarkdownRenderer(noColor);
colors = new ColorHelper(noColor, full);
cr = new ContentRenderer(colors, mdRenderer);
var dataParsers = new DataParsers(tail);
var pager = new InteractivePager(colors, cr, noColor, filePath, filterType, expandTools, tail);
var outputFormatters = new OutputFormatters(colors, full);
var statsAnalyzer = new StatsAnalyzer(colors, dataParsers.ParseJsonlData, dataParsers.ParseClaudeData, dataParsers.ParseWazaData);
var sessionStateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");
var sessionBrowser = new SessionBrowser(colors, cr, dataParsers, sessionStateDir);

// --- Stats command dispatch ---
if (cliArgs.Length > 0 && cliArgs[0] == "stats")
{
    var statsFilePaths = new List<string>();
    string? groupBy = null;
    string? filterModel = null;
    string? filterTask = null;
    int? failThreshold = null;
    bool statsJson = false;
    
    for (int i = 1; i < cliArgs.Length; i++)
    {
        switch (cliArgs[i])
        {
            case "--json":
                statsJson = true;
                break;
            case "--group-by":
                if (i + 1 < cliArgs.Length) groupBy = cliArgs[++i].ToLowerInvariant();
                else { Console.Error.WriteLine("Error: --group-by requires an argument (model or task)"); return; }
                break;
            case "--filter-model":
                if (i + 1 < cliArgs.Length) filterModel = cliArgs[++i];
                else { Console.Error.WriteLine("Error: --filter-model requires an argument"); return; }
                break;
            case "--filter-task":
                if (i + 1 < cliArgs.Length) filterTask = cliArgs[++i];
                else { Console.Error.WriteLine("Error: --filter-task requires an argument"); return; }
                break;
            case "--fail-threshold":
                if (i + 1 < cliArgs.Length && int.TryParse(cliArgs[++i], out int thresh)) failThreshold = thresh;
                else { Console.Error.WriteLine("Error: --fail-threshold requires a numeric argument (0-100)"); return; }
                break;
            default:
                if (cliArgs[i].StartsWith("-")) { Console.Error.WriteLine($"Unknown stats option: {cliArgs[i]}"); return; }
                statsFilePaths.Add(cliArgs[i]);
                break;
        }
    }
    
    if (statsFilePaths.Count == 0)
    {
        Console.Error.WriteLine("Error: stats command requires at least one file path or glob pattern");
        return;
    }
    
    // Expand globs and collect all files
    var allFiles = new List<string>();
    foreach (var pattern in statsFilePaths)
    {
        var expanded = ExpandGlob(pattern);
        if (expanded.Count == 0)
            Console.Error.WriteLine($"Warning: no files matched pattern: {pattern}");
        allFiles.AddRange(expanded);
    }
    
    if (allFiles.Count == 0)
    {
        if (statsJson)
        {
            statsAnalyzer.OutputStatsReport(new List<FileStats>(), groupBy, statsJson, failThreshold);
        }
        else
        {
            Console.Error.WriteLine("Error: no files found matching the specified patterns");
        }
        return;
    }
    
    // Process each file and extract stats
    var allStats = new List<FileStats>();
    foreach (var file in allFiles)
    {
        var stats = statsAnalyzer.ExtractStats(file);
        if (stats != null)
        {
            // Apply filters
            if (filterModel != null && !string.Equals(stats.Model, filterModel, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filterTask != null && !string.Equals(stats.TaskName, filterTask, StringComparison.OrdinalIgnoreCase))
                continue;
            allStats.Add(stats);
        }
    }
    
    // Aggregate and output
    statsAnalyzer.OutputStatsReport(allStats, groupBy, statsJson, failThreshold);
    return;
}

// Resolve session ID or browse sessions if no file given

if (filePath is not null && Guid.TryParse(filePath, out _) && !File.Exists(filePath))
{
    // Treat as session ID
    var sessionEventsPath = Path.Combine(sessionStateDir, filePath, "events.jsonl");
    if (File.Exists(sessionEventsPath))
        filePath = sessionEventsPath;
    else
    {
        Console.Error.WriteLine($"Error: No session found with ID {filePath}");
        return;
    }
}

// If dbPath is set, go directly to browser mode with that DB
if (dbPath is not null)
{
    if (Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("Error: Cannot use --db in redirected output");
        return;
    }
    filePath = sessionBrowser.BrowseSessions(dbPath);
    if (filePath is null) return;
    // Browser mode: loop between browser and pager
    bool browsing = true;
    while (browsing)
    {
        var action = OpenFile(filePath!);
        switch (action)
        {
            case PagerAction.Browse:
                filePath = sessionBrowser.BrowseSessions(dbPath);
                if (filePath is null) browsing = false;
                break;
            case PagerAction.Resume:
                sessionBrowser.LaunchResume(filePath!);
                browsing = false;
                break;
            default:
                browsing = false;
                break;
        }
    }
}
else if (filePath is null)
{
    if (Console.IsOutputRedirected)
    {
        Console.Error.WriteLine("Error: No file specified");
        PrintHelp();
        return;
    }
    filePath = sessionBrowser.BrowseSessions();
    if (filePath is null) return;
    // Browser mode: loop between browser and pager
    bool browsing = true;
    while (browsing)
    {
        var action = OpenFile(filePath!);
        switch (action)
        {
            case PagerAction.Browse:
                filePath = sessionBrowser.BrowseSessions();
                if (filePath is null) browsing = false;
                break;
            case PagerAction.Resume:
                sessionBrowser.LaunchResume(filePath!);
                browsing = false;
                break;
            default:
                browsing = false;
                break;
        }
    }
}
else
{
    OpenFile(filePath);
}

// --- Color helpers (Spectre.Console markup) ---
string Green(string s) => noColor ? s : $"[green]{Markup.Escape(s)}[/]";
string Yellow(string s) => noColor ? s : $"[yellow]{Markup.Escape(s)}[/]";
string Red(string s) => noColor ? s : $"[red]{Markup.Escape(s)}[/]";
string Dim(string s) => noColor ? s : $"[dim]{Markup.Escape(s)}[/]";
string Bold(string s) => noColor ? s : $"[bold]{Markup.Escape(s)}[/]";
string Cyan(string s) => noColor ? s : $"[cyan]{Markup.Escape(s)}[/]";

// --- OpenFile: format detection + pager, returns action ---
PagerAction OpenFile(string path)
{
    if (!File.Exists(path)) { Console.Error.WriteLine($"Error: File not found: {path}"); return PagerAction.Quit; }

    var firstLine = "";
    using (var reader = new StreamReader(path))
    {
        firstLine = reader.ReadLine() ?? "";
    }

    bool isJsonl = path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
        || (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && firstLine.TrimStart().StartsWith("{") && !firstLine.TrimStart().StartsWith("["));

    bool isClaude = false;
    if (isJsonl && IsClaudeFormat(path))
        isClaude = true;

    bool isEval = false;
    if (isJsonl && !isClaude && IsEvalFormat(path))
        isEval = true;

    bool isWaza = false;
    JsonDocument? wazaDoc = null;

    if (!isJsonl)
    {
        try
        {
            var jsonText = File.ReadAllText(path);
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
                return PagerAction.Quit;
            }
        }
        catch (JsonException)
        {
            Console.Error.WriteLine("Error: Could not determine file format. File is not valid JSON or JSONL.");
            return PagerAction.Quit;
        }
    }

    if (streamMode)
    {
        // Stream mode: original dump-everything behavior OR json/summary output
        if (summaryMode)
        {
            // Summary mode: high-level session stats
            if (isEval)
            {
                var parsed = ParseEvalData(path);
                if (parsed is not null)
                    OutputEvalSummary(parsed, jsonMode);
            }
            else if (isJsonl)
            {
                var parsed = isClaude ? dataParsers.ParseClaudeData(path) : dataParsers.ParseJsonlData(path);
                if (parsed is not null)
                    outputFormatters.OutputSummary(parsed, jsonMode);
            }
            else if (isWaza)
            {
                var parsed = dataParsers.ParseWazaData(wazaDoc!);
                outputFormatters.OutputWazaSummary(parsed, jsonMode);
            }
        }
        else if (jsonMode)
        {
            // JSON mode: structured JSONL output
            if (isEval)
            {
                var parsed = ParseEvalData(path);
                if (parsed is not null)
                    OutputEvalSummary(parsed, true);
            }
            else if (isJsonl)
            {
                var parsed = isClaude ? dataParsers.ParseClaudeData(path) : dataParsers.ParseJsonlData(path);
                if (parsed is not null)
                    outputFormatters.OutputJsonl(parsed, filterType, expandTools);
            }
            else if (isWaza)
            {
                var parsed = dataParsers.ParseWazaData(wazaDoc!);
                outputFormatters.OutputWazaJsonl(parsed, filterType, expandTools);
            }
        }
        else
        {
            // Standard stream mode
            if (isEval) StreamEvalEvents(path);
            else if (isJsonl && !isClaude) StreamEventsJsonl(path);
            else if (isJsonl && isClaude) { var p = dataParsers.ParseClaudeData(path); if (p != null) { var cl = cr!.RenderJsonlContentLines(p, filterType, expandTools); foreach (var l in cl) Console.WriteLine(l); } }
            else if (isWaza) StreamWazaTranscript(wazaDoc!);
        }
        return PagerAction.Quit;
    }
    else
    {
        // Interactive pager mode (default)
        List<string> headerLines;
        List<string> contentLines;
        if (isEval)
        {
            var parsed = ParseEvalData(path);
            if (parsed is null) return PagerAction.Quit;
            headerLines = cr!.RenderEvalHeaderLines(parsed);
            contentLines = cr!.RenderEvalContentLines(parsed, filterType, expandTools);
            return pager.Run(headerLines, contentLines, parsed, isJsonlFormat: true, noFollow: noFollow);
        }
        else if (isJsonl)
        {
            var parsed = isClaude ? dataParsers.ParseClaudeData(path) : dataParsers.ParseJsonlData(path);
            if (parsed is null) return PagerAction.Quit;
            headerLines = cr!.RenderJsonlHeaderLines(parsed);
            contentLines = cr!.RenderJsonlContentLines(parsed, filterType, expandTools);
            return pager.Run(headerLines, contentLines, parsed, isJsonlFormat: !isClaude, noFollow: isClaude || noFollow);
        }
        else if (isWaza)
        {
            var parsed = dataParsers.ParseWazaData(wazaDoc!);
            headerLines = cr!.RenderWazaHeaderLines(parsed);
            contentLines = cr!.RenderWazaContentLines(parsed, filterType, expandTools);
            return pager.Run(headerLines, contentLines, parsed, isJsonlFormat: false, noFollow: true);
        }
        return PagerAction.Quit;
    }
}

// --- LaunchResume: detect session type and launch CLI ---
void StreamEvalEvents(string path)
{
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch { continue; }

        var root = doc.RootElement;
        var evType = SafeGetString(root, "type");
        var data = root.TryGetProperty("data", out var d) ? d : default;
        var ts = SafeGetString(root, "ts");
        var timeStr = ts.Length > 19 ? ts[11..19] : ts;

        switch (evType)
        {
            case "eval.start":
                Console.WriteLine($"\n{Bold(Cyan($"📋 Eval Suite: {SafeGetString(data, "suite")}"))} ({SafeGetString(data, "case_count")} cases)");
                break;
            case "case.start":
                Console.WriteLine($"\n{Bold($"━━━ {SafeGetString(data, "case")}")} {Dim(timeStr)}");
                Console.WriteLine(Dim($"    Prompt: {SafeGetString(data, "prompt")[..Math.Min(100, SafeGetString(data, "prompt").Length)]}..."));
                break;
            case "tool.start":
                Console.Write($"    {Yellow("⚡")} {SafeGetString(data, "tool_name")}");
                break;
            case "tool.complete":
                Console.WriteLine(Dim($" ({SafeGetString(data, "duration_ms")}ms)"));
                break;
            case "assertion.result":
                var apassed = data.TryGetProperty("passed", out var ap) && ap.GetBoolean();
                Console.WriteLine(apassed
                    ? Green($"    ✅ {SafeGetString(data, "feedback")}")
                    : Red($"    ❌ {SafeGetString(data, "feedback")}"));
                break;
            case "case.complete":
                var cpassed = data.TryGetProperty("passed", out var cp) && cp.GetBoolean();
                var cdur = data.TryGetProperty("duration_ms", out var cd) ? cd.GetDouble() : 0;
                Console.WriteLine(cpassed
                    ? Green($"  ✅ PASS ({cdur / 1000.0:F1}s)")
                    : Red($"  ❌ FAIL ({cdur / 1000.0:F1}s)"));
                break;
            case "eval.complete":
                var ep2 = data.TryGetProperty("passed", out var ep2v) ? ep2v.GetInt32() : 0;
                var ef2 = data.TryGetProperty("failed", out var ef2v) ? ef2v.GetInt32() : 0;
                Console.WriteLine($"\n{Bold("Results:")} {Green($"✅{ep2}")} {Red($"❌{ef2}")}");
                break;
            case "error":
                Console.WriteLine(Red($"  ⚠ {SafeGetString(data, "message")}"));
                break;
        }
    }
}

void OutputEvalSummary(EvalData d, bool asJson)
{
    if (asJson)
    {
        var summary = new
        {
            suite = d.Suite,
            description = d.Description,
            cases = d.Cases.Select(c => new
            {
                name = c.Name,
                passed = c.Passed,
                duration_ms = c.DurationMs,
                tool_calls = c.ToolCallCount,
                tools = c.ToolsUsed,
                response_length = c.ResponseLength,
                feedback = c.AssertionFeedback,
                error = c.Error
            }),
            total_passed = d.TotalPassed,
            total_failed = d.TotalFailed,
            total_skipped = d.TotalSkipped,
            total_duration_ms = d.TotalDurationMs,
            total_tool_calls = d.TotalToolCalls
        };
        Console.WriteLine(JsonSerializer.Serialize(summary, ColorHelper.SummarySerializer));
    }
    else
    {
        Console.WriteLine($"\n📋 Eval Suite: {d.Suite}");
        if (d.Description != "") Console.WriteLine($"   {d.Description.TrimEnd()}");
        Console.WriteLine($"   Results: ✅{d.TotalPassed} ❌{d.TotalFailed} ⏭{d.TotalSkipped}");
        Console.WriteLine($"   Duration: {d.TotalDurationMs / 1000.0:F1}s  Tools: {d.TotalToolCalls}");
        Console.WriteLine();
        foreach (var c in d.Cases)
        {
            var badge = c.Passed == true ? "✅" : c.Passed == false ? "❌" : "⏳";
            Console.WriteLine($"   {badge} {c.Name} ({c.DurationMs / 1000.0:F1}s, {c.ToolCallCount} tools)");
            if (c.Error is not null) Console.WriteLine($"      Error: {c.Error}");
        }
    }
}

// ========== Stream Mode (original dump behavior) ==========
void StreamEventsJsonl(string path)
{
    var parsed = dataParsers.ParseJsonlData(path);
    if (parsed is null) return;
    var d = parsed;

    // Header card
    foreach (var line in cr!.RenderJsonlHeaderLines(d))
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }

    // Content
    foreach (var line in cr!.RenderJsonlContentLines(d, filterType, expandTools))
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }
}

void StreamWazaTranscript(JsonDocument doc)
{
    var d = dataParsers.ParseWazaData(doc);
    foreach (var line in cr!.RenderWazaHeaderLines(d))
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }
    foreach (var line in cr!.RenderWazaContentLines(d, filterType, expandTools))
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }
}

// ========== JSON and Summary Output Modes ==========

void PrintHelp()
{
    Console.WriteLine("""

    Usage: replay [file|session-id] [options]
          replay --db <path>
          replay stats <files...> [options]

      <file>              Path to .jsonl or .json transcript file, or .db file
      <session-id>        GUID of a Copilot CLI session to open
      (no args)           Browse recent Copilot CLI sessions

    Options:
      --db <path>         Browse sessions from an external session-store.db file
      --tail <N>          Show only the last N conversation turns
      --expand-tools      Show tool arguments, results, and thinking/reasoning
      --full              Don't truncate tool output (use with --expand-tools)
      --filter <type>     Filter by event type: user, assistant, tool, error
      --no-color          Disable ANSI colors
      --stream            Use streaming output (non-interactive, original behavior)
      --no-follow         Disable auto-follow for JSONL files
      --json              Output as structured JSONL (one JSON object per line)
      --summary           Show high-level session statistics instead of full transcript
      --help              Show this help

    Stats Command:
      replay stats <files...>  Aggregate statistics across multiple transcript files
      
      Options:
        --json               Output stats as JSON
        --group-by <field>   Group results by 'model' or 'task'
        --filter-model <m>   Only include files matching this model
        --filter-task <t>    Only include files matching this task name
        --fail-threshold <N> Exit with code 1 if pass rate < N% (0-100)
      
      Examples:
        replay stats results/*.json
        replay stats results/*.json --group-by model
        replay stats results/*.json --json
        replay stats results/*.json --filter-model sonnet-4 --fail-threshold 80

    Output Modes:
      --json              Emit JSONL format (composable with --filter, --tail, --expand-tools)
                          Each turn outputs: {"turn": N, "role": "user|assistant|tool", ...}
      --summary           Show session overview: duration, turn counts, tools used, errors
      --summary --json    Combine for machine-readable summary in JSON format

    Interactive mode (default):
      ↑/k ↓/j             Scroll up/down one line
      ←/h  →/l             Scroll left/right (pan)
      PgUp PgDn             Page up/down
      0                     Reset horizontal scroll
      Space                Page down
      g/Home  G/End        Jump to start/end
      t                    Toggle tool expansion
      f                    Cycle filter: all → user → assistant → tool → error
      i                    Toggle full session info overlay
      /                    Search (Enter to execute, Esc to cancel)
      n/N                  Next/previous search match
      b                    Back to session browser
      r                    Resume session (launches copilot or claude CLI)
      q/Escape             Quit

    JSONL files auto-follow by default (like tail -f). Use --no-follow to disable.
    When output is piped (redirected), stream mode is used automatically.
    """);
}


