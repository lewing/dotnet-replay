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
var outputFormatters = new OutputFormatters(colors, full);
var statsAnalyzer = new StatsAnalyzer(colors, dataParsers.ParseJsonlData, dataParsers.ParseClaudeData, dataParsers.ParseWazaData);

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
var sessionStateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

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
    filePath = BrowseSessions(sessionStateDir, dbPath);
    if (filePath is null) return;
    // Browser mode: loop between browser and pager
    bool browsing = true;
    while (browsing)
    {
        var action = OpenFile(filePath!);
        switch (action)
        {
            case PagerAction.Browse:
                filePath = BrowseSessions(sessionStateDir, dbPath);
                if (filePath is null) browsing = false;
                break;
            case PagerAction.Resume:
                LaunchResume(filePath!);
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
    filePath = BrowseSessions(sessionStateDir);
    if (filePath is null) return;
    // Browser mode: loop between browser and pager
    bool browsing = true;
    while (browsing)
    {
        var action = OpenFile(filePath!);
        switch (action)
        {
            case PagerAction.Browse:
                filePath = BrowseSessions(sessionStateDir);
                if (filePath is null) browsing = false;
                break;
            case PagerAction.Resume:
                LaunchResume(filePath!);
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
            return RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: true, noFollow: noFollow);
        }
        else if (isJsonl)
        {
            var parsed = isClaude ? dataParsers.ParseClaudeData(path) : dataParsers.ParseJsonlData(path);
            if (parsed is null) return PagerAction.Quit;
            headerLines = cr!.RenderJsonlHeaderLines(parsed);
            contentLines = cr!.RenderJsonlContentLines(parsed, filterType, expandTools);
            return RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: !isClaude, noFollow: isClaude || noFollow);
        }
        else if (isWaza)
        {
            var parsed = dataParsers.ParseWazaData(wazaDoc!);
            headerLines = cr!.RenderWazaHeaderLines(parsed);
            contentLines = cr!.RenderWazaContentLines(parsed, filterType, expandTools);
            return RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: false, noFollow: true);
        }
        return PagerAction.Quit;
    }
}

// --- LaunchResume: detect session type and launch CLI ---
void LaunchResume(string path)
{
    // Determine if this is a Claude or Copilot session
    bool isClaude = path.Contains(Path.Combine(".claude", "projects"));
    string? sessionId = null;

    if (isClaude)
    {
        // Claude sessions: the filename (without extension) is the session ID
        sessionId = Path.GetFileNameWithoutExtension(path);
    }
    else
    {
        // Copilot sessions: the directory name is the session ID
        sessionId = Path.GetFileName(Path.GetDirectoryName(path));
    }

    if (string.IsNullOrEmpty(sessionId))
    {
        Console.Error.WriteLine("Error: Could not determine session ID for resume.");
        return;
    }

    Console.CursorVisible = true;
    AnsiConsole.Clear();

    string command, args;
    if (isClaude)
    {
        command = "claude";
        args = $"--resume \"{sessionId}\"";
    }
    else
    {
        command = "copilot";
        args = $"--resume \"{sessionId}\"";
    }

    Console.WriteLine($"Resuming session with: {command} {args}");
    Console.WriteLine();

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            UseShellExecute = false
        };
        var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error launching {command}: {ex.Message}");
        Console.Error.WriteLine($"Make sure '{command}' is installed and available in your PATH.");
    }
}

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

// ========== Interactive Pager ==========
PagerAction RunInteractivePager<T>(List<string> headerLines, List<string> contentLines, T parsedData, bool isJsonlFormat, bool noFollow)
{
    int scrollOffset = 0;
    int scrollX = 0;
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
    int lastWidth = AnsiConsole.Profile.Width;
    int lastHeight = AnsiConsole.Profile.Height;
    bool needsFullClear = true; // first render does a full clear

    // Build compact info bar
    string infoBar;
    if (parsedData is JsonlData jData)
        infoBar = cr!.BuildJsonlInfoBar(jData) + (following ? " ↓ FOLLOWING" : "");
    else if (parsedData is EvalData eData)
        infoBar = cr!.BuildEvalInfoBar(eData) + (following ? " ↓ FOLLOWING" : "");
    else if (parsedData is WazaData wData)
        infoBar = cr!.BuildWazaInfoBar(wData, filePath ?? "");
    else
        infoBar = $"[{Path.GetFileName(filePath)}]";

    string[] filterCycle = ["all", "user", "assistant", "tool", "error"];
    int filterIndex = currentFilter is null ? 0 : Array.IndexOf(filterCycle, currentFilter);
    if (filterIndex < 0) filterIndex = 0;

    string StripAnsi(string s) => GetVisibleText(s);
    string HighlightLine(string s) => noColor ? s : $"[on cyan black]{Markup.Escape(GetVisibleText(s))}[/]";

    string? GetScrollAnchor(List<string> lines, int offset)
    {
        if (offset >= lines.Count) return null;
        for (int i = 0; i < 5 && offset + i < lines.Count; i++)
        {
            var text = StripAnsi(lines[offset + i]);
            if (!string.IsNullOrWhiteSpace(text) &&
                !text.TrimStart().StartsWith("───") &&
                text.Trim() != "┃" &&
                text.Length > 5)
                return text;
        }
        return StripAnsi(lines[offset]);
    }

    int FindAnchoredOffset(List<string> lines, string? anchorText, int fallbackOffset, int oldCount)
    {
        if (anchorText == null) return fallbackOffset;
        for (int si = 0; si < lines.Count; si++)
        {
            if (StripAnsi(lines[si]) == anchorText)
                return si;
        }
        if (oldCount > 0)
            return Math.Min((int)((long)fallbackOffset * lines.Count / oldCount), Math.Max(0, lines.Count - 1));
        return fallbackOffset;
    }

    void RebuildContent()
    {
        var filter = filterIndex == 0 ? null : filterCycle[filterIndex];
        if (parsedData is JsonlData jd)
            contentLines = cr!.RenderJsonlContentLines(jd, filter, currentExpandTools);
        else if (parsedData is EvalData ed)
            contentLines = cr!.RenderEvalContentLines(ed, filter, currentExpandTools);
        else if (parsedData is WazaData wd)
            contentLines = cr!.RenderWazaContentLines(wd, filter, currentExpandTools);
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
        int h = AnsiConsole.Profile.Height;
        // 1 info bar line + 1 status bar line = 2 chrome lines
        return Math.Max(1, h - 2);
    }

    void WriteMarkupLine(int row, string markupText, int width, int hOffset = 0)
    {
        AnsiConsole.Cursor.SetPosition(0, row + 1); // Spectre uses 1-based positions
        
        // Apply horizontal scroll offset
        var scrolledMarkup = hOffset > 0 ? SkipMarkupWidth(markupText, hOffset) : markupText;

        var visible = StripAnsi(scrolledMarkup);
        int visWidth = VisibleWidth(visible);
        
        if (visWidth >= width)
        {
            if (noColor)
            {
                var truncated = TruncateToWidth(visible, width - 1) + "…";
                Console.Write(truncated);
            }
            else
            {
                var truncatedMarkup = TruncateMarkupToWidth(scrolledMarkup, width - 1);
                try { AnsiConsole.Markup(truncatedMarkup); }
                catch
                {
                    var truncated = TruncateToWidth(visible, width - 1) + "…";
                    Console.Write(truncated);
                }
            }
        }
        else
        {
            int padding = width - visWidth;
            if (noColor)
                Console.Write(visible + new string(' ', padding));
            else
            {
                try { AnsiConsole.Markup(scrolledMarkup + new string(' ', padding)); }
                catch { Console.Write(visible + new string(' ', padding)); }
            }
        }
    }

    void Render()
    {
        int w = AnsiConsole.Profile.Width;
        int h = AnsiConsole.Profile.Height;

        // Detect terminal resize — just update dimensions, no clear needed
        // since we overwrite every line including padding
        if (w != lastWidth || h != lastHeight)
        {
            lastWidth = w;
            lastHeight = h;
        }

        if (needsFullClear)
        {
            AnsiConsole.Clear();
            needsFullClear = false;
        }

        Console.CursorVisible = false;

        int row = 0;

        // Row 0: Compact info bar (inverted)
        string topBarContent = " " + Markup.Escape(infoBar);
        int topBarVisLen = VisibleWidth(topBarContent.Replace("[[", "[").Replace("]]", "]"));
        if (topBarVisLen < w)
            topBarContent += new string(' ', w - topBarVisLen);
        AnsiConsole.Cursor.SetPosition(0, 1); // 1-based
        if (noColor)
            Console.Write(topBarContent);
        else
        {
            try { AnsiConsole.Markup($"[invert]{topBarContent}[/]"); }
            catch { Console.Write(topBarContent.Replace("[[", "[").Replace("]]", "]")); }
        }
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
                WriteMarkupLine(row, headerLines[i], w);
                row++;
            }
            // Fill rest of viewport
            for (int i = overlayLines; i < vpHeight; i++)
            {
                AnsiConsole.Cursor.SetPosition(0, row + 1);
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
                WriteMarkupLine(row, isMatch ? HighlightLine(line) : line, w, scrollX);
                row++;
            }

            // Fill remaining viewport with blank lines
            int rendered = end - scrollOffset;
            for (int i = rendered; i < vpHeight; i++)
            {
                AnsiConsole.Cursor.SetPosition(0, row + 1);
                Console.Write(new string(' ', w));
                row++;
            }
        }

        // Status bar (bottom line)
        var statusFilter = filterIndex == 0 ? "all" : filterCycle[filterIndex];
        int currentLine = contentLines.Count == 0 ? 0 : scrollOffset + 1;
        string statusText;
        if (showInfoOverlay)
        {
            statusText = " Press i or any key to dismiss";
        }
        else if (inSearchMode)
        {
            statusText = $" Search: {searchBuffer}_";
        }
        else if (searchPattern is not null && searchMatches.Count > 0)
        {
            statusText = $" Search: \"{searchPattern}\" ({searchMatchIndex + 1}/{searchMatches.Count}) | n/N next/prev | Esc clear";
        }
        else
        {
            var followIndicator = following ? (userAtBottom ? " LIVE" : " [new content ↓]") : "";
            var colIndicator = scrollX > 0 ? $" Col {scrollX}+" : "";
            statusText = $" Line {currentLine}/{contentLines.Count}{colIndicator} | Filter: {statusFilter}{followIndicator} | t tools | b browse | r resume | q quit";
        }
        var escapedStatus = Markup.Escape(statusText);
        int statusVisLen = VisibleWidth(statusText);
        if (statusVisLen < w)
            escapedStatus += new string(' ', w - statusVisLen);
        AnsiConsole.Cursor.SetPosition(0, row + 1);
        if (noColor)
            Console.Write(statusText + (VisibleWidth(statusText) < w ? new string(' ', w - VisibleWidth(statusText)) : ""));
        else
        {
            try { AnsiConsole.Markup($"[invert]{escapedStatus}[/]"); }
            catch { Console.Write(statusText + (VisibleWidth(statusText) < w ? new string(' ', w - VisibleWidth(statusText)) : "")); }
        }

        Console.CursorVisible = true;
    }

    // Handle Ctrl+C gracefully
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.CursorVisible = true;
        AnsiConsole.Clear();
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
                            List<string> newLines = [];
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
                                infoBar = cr!.BuildJsonlInfoBar(jdFollow) + " ↓ FOLLOWING";
                                if (wasAtBottom && userAtBottom)
                                {
                                    scrollOffset = Math.Max(0, contentLines.Count - ViewportHeight());
                                }
                                Render();
                            }
                        }
                        else if (fi.Length > lastFileOffset && parsedData is EvalData edFollow)
                        {
                            List<string> newLines = [];
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
                                    ProcessEvalEvent(edFollow, rawLine);
                                RebuildContent();
                                infoBar = cr!.BuildEvalInfoBar(edFollow) + " ↓ FOLLOWING";
                                if (wasAtBottom && userAtBottom)
                                    scrollOffset = Math.Max(0, contentLines.Count - ViewportHeight());
                                Render();
                            }
                        }
                    }
                    catch { /* ignore read errors, will retry on next change */ }
                }
            }

            if (!Console.KeyAvailable)
            {
                // Check for terminal resize
                int curW = AnsiConsole.Profile.Width;
                int curH = AnsiConsole.Profile.Height;
                if (curW != lastWidth || curH != lastHeight)
                {
                    lastWidth = curW;
                    lastHeight = curH;
                    // Rebuild header/content since header boxes are now width-dependent
                    if (parsedData is JsonlData jdResize)
                        headerLines = cr!.RenderJsonlHeaderLines(jdResize);
                    else if (parsedData is EvalData edResize)
                        headerLines = cr!.RenderEvalHeaderLines(edResize);
                    else if (parsedData is WazaData wdResize)
                        headerLines = cr!.RenderWazaHeaderLines(wdResize);
                    RebuildContent();
                    needsFullClear = true;
                    Render();
                }
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
                    return PagerAction.Quit;

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
                    scrollX = Math.Max(0, scrollX - 8);
                    Render();
                    break;

                case ConsoleKey.PageUp:
                    scrollOffset = Math.Max(0, scrollOffset - ViewportHeight());
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.RightArrow:
                    scrollX += 8;
                    Render();
                    break;

                case ConsoleKey.PageDown:
                case ConsoleKey.Spacebar:
                    scrollOffset += ViewportHeight();
                    ClampScroll();
                    userAtBottom = scrollOffset >= Math.Max(0, contentLines.Count - ViewportHeight());
                    Render();
                    break;

                case ConsoleKey.Home:
                    scrollOffset = 0;
                    scrollX = 0;
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
                            scrollX = Math.Max(0, scrollX - 8);
                            Render();
                            break;
                        case 'l':
                            scrollX += 8;
                            Render();
                            break;
                        case '0':
                            scrollX = 0;
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
                            var tAnchor = GetScrollAnchor(contentLines, scrollOffset);
                            int tOldCount = contentLines.Count;
                            currentExpandTools = !currentExpandTools;
                            RebuildContent();
                            scrollOffset = FindAnchoredOffset(contentLines, tAnchor, scrollOffset, tOldCount);
                            ClampScroll();
                            Render();
                            break;
                        case 'f':
                            var fAnchor = GetScrollAnchor(contentLines, scrollOffset);
                            int fOldCount = contentLines.Count;
                            filterIndex = (filterIndex + 1) % filterCycle.Length;
                            RebuildContent();
                            scrollOffset = FindAnchoredOffset(contentLines, fAnchor, scrollOffset, fOldCount);
                            ClampScroll();
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
                        case 'b':
                            return PagerAction.Browse;
                        case 'r':
                            return PagerAction.Resume;
                    }
                    break;
            }
        }
    }
    finally
    {
        watcher?.Dispose();
        Console.CursorVisible = true;
        AnsiConsole.Clear();
    }
    return PagerAction.Quit;
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

// Try to load sessions from the Copilot CLI's SQLite session-store.db (read-only).
// Returns null if the DB doesn't exist, has wrong schema, or any error occurs.
List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)>? LoadSessionsFromDb(string sessionStateDir, string? dbPathOverride = null)
{
    var dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir)!, "session-store.db");
    if (!File.Exists(dbPath)) return null;

    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // Validate schema version
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
            if (cmd.ExecuteScalar() == null) return null;
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
            var ver = cmd.ExecuteScalar();
            if (ver == null || Convert.ToInt32(ver) != 1) return null;
        }

        // Validate sessions table has expected columns
        var expectedCols = new HashSet<string> { "id", "cwd", "summary", "updated_at", "branch", "repository" };
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(sessions)";
            var actualCols = new HashSet<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) actualCols.Add(reader.GetString(1));
            if (!expectedCols.IsSubsetOf(actualCols)) return null;
        }

        // Load sessions
        var results = new List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, cwd, summary, updated_at, branch, repository FROM sessions ORDER BY updated_at DESC";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var cwd = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var summary = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var updatedStr = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var branch = reader.IsDBNull(4) ? "" : reader.GetString(4);
                var repository = reader.IsDBNull(5) ? "" : reader.GetString(5);

                DateTime.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt);

                var eventsPath = Path.Combine(sessionStateDir, id, "events.jsonl");
                long fileSize = 0;
                if (File.Exists(eventsPath))
                    try { fileSize = new FileInfo(eventsPath).Length; } catch { }
                else
                    continue; // skip DB entries without local transcript files

                results.Add((id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
            }
        }
        return results;
    }
    catch
    {
        return null; // any error → fall back to file scan
    }
}

string? BrowseSessions(string sessionStateDir, string? dbPathOverride = null)
{
    // When using external DB, skip directory check
    if (dbPathOverride == null && !Directory.Exists(sessionStateDir))
    {
        Console.Error.WriteLine("No Copilot session directory found.");
        Console.Error.WriteLine($"Expected: {sessionStateDir}");
        return null;
    }

    List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)> allSessions = [];
    var sessionsLock = new System.Threading.Lock();
    bool scanComplete = false;
    int lastRenderedCount = -1;

    // Background scan thread — try DB first, fall back to file scan
    var scanThread = new Thread(() =>
    {
        // Try loading Copilot sessions from SQLite DB (fast path)
        var dbSessions = LoadSessionsFromDb(sessionStateDir, dbPathOverride);
        string? dbPath = null;
        var knownSessionIds = new HashSet<string>();
        DateTime lastUpdatedAt = DateTime.MinValue;

        if (dbSessions != null)
        {
            // Record the DB path for polling
            dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir)!, "session-store.db");
            
            lock (sessionsLock)
            {
                allSessions.AddRange(dbSessions);
                allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
                foreach (var s in dbSessions)
                {
                    knownSessionIds.Add(s.id);
                    if (s.updatedAt > lastUpdatedAt) lastUpdatedAt = s.updatedAt;
                }
            }
        }
        else if (dbPathOverride == null)
        {
            // Fallback: scan filesystem for Copilot sessions (only when DB not available)
            foreach (var dir in Directory.GetDirectories(sessionStateDir))
            {
                var yamlPath = Path.Combine(dir, "workspace.yaml");
                var eventsPath = Path.Combine(dir, "events.jsonl");
                if (!File.Exists(yamlPath) || !File.Exists(eventsPath)) continue;

                var props = new Dictionary<string, string>();
                try
                {
                    foreach (var line in File.ReadLines(yamlPath))
                    {
                        var colonIdx = line.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            var key = line[..colonIdx].Trim();
                            var value = line[(colonIdx + 1)..].Trim().Trim('"');
                            props[key] = value;
                        }
                    }
                }
                catch { continue; }

                var id = props.GetValueOrDefault("id", Path.GetFileName(dir));
                var summary = props.GetValueOrDefault("summary", "");
                var cwd = props.GetValueOrDefault("cwd", "");
                var updatedStr = props.GetValueOrDefault("updated_at", "");
                DateTime.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt);

                long fileSize = 0;
                try { fileSize = new FileInfo(eventsPath).Length; } catch { }

                lock (sessionsLock)
                {
                    allSessions.Add((id, summary, cwd, updatedAt, eventsPath, fileSize, "", ""));
                    knownSessionIds.Add(id);
                    if (allSessions.Count % 50 == 0)
                        allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
                }
            }
            lock (sessionsLock)
            {
                allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
            }
        }
        
        // Always scan Claude Code sessions (no DB available), skip if using external DB
        if (dbPathOverride == null)
        {
            var claudeProjectsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (Directory.Exists(claudeProjectsDir))
            {
            foreach (var projDir in Directory.GetDirectories(claudeProjectsDir))
            {
                foreach (var jsonlFile in Directory.GetFiles(projDir, "*.jsonl"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(jsonlFile);
                    if (!Guid.TryParse(fileName, out _)) continue;

                    try
                    {
                        string claudeId = fileName, claudeSummary = "", claudeCwd = "";
                        DateTime claudeUpdatedAt = File.GetLastWriteTimeUtc(jsonlFile);
                        foreach (var line in File.ReadLines(jsonlFile).Take(5))
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            if (claudeCwd == "") claudeCwd = SafeGetString(root, "cwd");
                            if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                            {
                                var text = c.GetString() ?? "";
                                if (text.Length > 0 && claudeSummary == "")
                                    claudeSummary = text.Length > 80 ? text[..77] + "..." : text;
                            }
                            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var msTs))
                                claudeUpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(msTs).UtcDateTime;
                        }
                        if (claudeSummary == "") claudeSummary = Path.GetFileName(projDir).Replace("-", "\\");

                        long fileSize = new FileInfo(jsonlFile).Length;
                        lock (sessionsLock)
                        {
                            allSessions.Add((claudeId, claudeSummary, claudeCwd, claudeUpdatedAt, jsonlFile, fileSize, "", ""));
                            if (allSessions.Count % 50 == 0)
                                allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
                        }
                    }
                    catch { continue; }
                }
            }
        }
        lock (sessionsLock)
        {
            allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
        }
        }
        scanComplete = true;
        
        // If we loaded from DB, poll for new sessions every 5 seconds
        if (dbPath != null)
        {
            while (true)
            {
                Thread.Sleep(5000);
                
                try
                {
                    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id, cwd, summary, updated_at, branch, repository FROM sessions WHERE updated_at > @lastUpdated ORDER BY updated_at DESC";
                    cmd.Parameters.AddWithValue("@lastUpdated", lastUpdatedAt.ToString("o"));
                    
                    using var reader = cmd.ExecuteReader();
                    var newSessions = new List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)>();
                    
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        if (knownSessionIds.Contains(id)) continue; // skip duplicates
                        
                        var cwd = reader.IsDBNull(1) ? "" : reader.GetString(1);
                        var summary = reader.IsDBNull(2) ? "" : reader.GetString(2);
                        var updatedStr = reader.IsDBNull(3) ? "" : reader.GetString(3);
                        var branch = reader.IsDBNull(4) ? "" : reader.GetString(4);
                        var repository = reader.IsDBNull(5) ? "" : reader.GetString(5);
                        
                        DateTime.TryParse(updatedStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt);
                        
                        var eventsPath = Path.Combine(sessionStateDir, id, "events.jsonl");
                        long fileSize = 0;
                        if (File.Exists(eventsPath))
                            try { fileSize = new FileInfo(eventsPath).Length; } catch { }
                        else
                            continue;
                        
                        newSessions.Add((id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
                        knownSessionIds.Add(id);
                        if (updatedAt > lastUpdatedAt) lastUpdatedAt = updatedAt;
                    }
                    
                    if (newSessions.Count > 0)
                    {
                        lock (sessionsLock)
                        {
                            allSessions.AddRange(newSessions);
                            allSessions.Sort((a, b) => b.updatedAt.CompareTo(a.updatedAt));
                        }
                    }
                }
                catch { /* ignore polling errors */ }
            }
        }
    });
    scanThread.IsBackground = true;
    scanThread.Start();

    // Interactive UI state
    int cursorIdx = 0;
    int scrollTop = 0;
    bool inSearch = false;
    string searchFilter = "";
    var searchBuf = new StringBuilder();
    bool showPreview = false;
    string? previewSessionId = null;
    List<string> previewLines = new();
    int previewScroll = 0;
    List<int> filtered = new(); // indices into allSessions

    void RebuildFiltered()
    {
        filtered.Clear();
        lock (sessionsLock)
        {
            for (int i = 0; i < allSessions.Count; i++)
            {
                if (searchFilter.Length > 0)
                {
                    var s = allSessions[i];
                    var text = $"{s.summary} {s.cwd} {s.id} {s.branch} {s.repository}";
                    if (!text.Contains(searchFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                filtered.Add(i);
            }
        }
    }

    int ViewHeight()
    {
        try { return Math.Max(3, AnsiConsole.Profile.Height - 3); }
        catch { return 20; }
    }

    void ClampCursor()
    {
        if (filtered.Count == 0) { cursorIdx = 0; scrollTop = 0; return; }
        cursorIdx = Math.Max(0, Math.Min(cursorIdx, filtered.Count - 1));
        int vh = ViewHeight();
        if (cursorIdx < scrollTop) scrollTop = cursorIdx;
        if (cursorIdx >= scrollTop + vh) scrollTop = cursorIdx - vh + 1;
        scrollTop = Math.Max(0, scrollTop);
    }

    void LoadPreview()
    {
        if (filtered.Count == 0 || cursorIdx >= filtered.Count) { previewLines.Clear(); return; }
        string eventsPath;
        string id;
        lock (sessionsLock)
        {
            var s = allSessions[filtered[cursorIdx]];
            eventsPath = s.eventsPath;
            id = s.id;
        }
        if (id == previewSessionId) return;
        previewSessionId = id;
        previewScroll = 0;
        try
        {
            var data = IsClaudeFormat(eventsPath) ? dataParsers.ParseClaudeData(eventsPath) : dataParsers.ParseJsonlData(eventsPath);
            if (data == null) { previewLines = ["", "  (unable to load preview)"]; return; }
            if (data.Turns.Count > 50)
                data = data with { Turns = data.Turns.Skip(data.Turns.Count - 50).ToList() };
            previewLines = cr!.RenderJsonlContentLines(data, null, false);
            previewScroll = Math.Max(0, previewLines.Count - ViewHeight());
        }
        catch
        {
            previewLines = ["", "  (unable to load preview)"];
            previewScroll = 0;
        }
    }

    void Render()
    {
        int w;
        try { w = AnsiConsole.Profile.Width; } catch { w = 80; }
        int vh = ViewHeight();
        int listWidth = showPreview ? Math.Max(30, w * 2 / 5) : w;
        int previewWidth = w - listWidth;

        if (showPreview) LoadPreview();

        Console.CursorVisible = false;
        AnsiConsole.Cursor.SetPosition(0, 1);

        // Header
        int count;
        lock (sessionsLock) { count = allSessions.Count; }
        var loadingStatus = scanComplete ? "" : " Loading...";
        var filterStatus = searchFilter.Length > 0 ? $" filter: \"{searchFilter}\" ({filtered.Count} matches)" : "";
        var cursorInfo = "";
        if (filtered.Count > 0 && cursorIdx < filtered.Count)
        {
            lock (sessionsLock)
            {
                var cs = allSessions[filtered[cursorIdx]];
                var updated = cs.updatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                cursorInfo = $" | {cs.id} {updated}";
            }
        }
        var headerBase = $" 📋 Sessions — {count} sessions{loadingStatus}{filterStatus}";
        var headerText = headerBase + cursorInfo;
        int headerVis = VisibleWidth(headerText);
        var hdrPad = headerVis < w ? new string(' ', w - headerVis) : "";
        try
        {
            var escapedBase = Markup.Escape(headerBase);
            var escapedCursor = Markup.Escape(cursorInfo);
            AnsiConsole.Markup($"[bold invert]{escapedBase}[/][bold underline invert]{escapedCursor}[/][bold invert]{hdrPad}[/]");
        }
        catch { Console.Write(headerText + hdrPad); }

        // Content rows
        for (int vi = scrollTop; vi < scrollTop + vh; vi++)
        {
            AnsiConsole.Cursor.SetPosition(0, vi - scrollTop + 2);

            // Left side: session list
            if (vi < filtered.Count)
            {
                string id, summary, cwd, eventsPath, branch, repository;
                DateTime updatedAt;
                long fileSize;
                lock (sessionsLock)
                {
                    var si = filtered[vi];
                    (id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository) = allSessions[si];
                }
                var age = FormatAge(DateTime.UtcNow - updatedAt);
                var size = FormatFileSize(fileSize);
                var icon = eventsPath.Contains(".claude") ? "🔴" : "🤖";
                var branchTag = !string.IsNullOrEmpty(branch) ? $" [{branch}]" : "";
                var display = !string.IsNullOrEmpty(summary) ? summary : cwd;
                int maxDisplay = Math.Max(10, listWidth - 21 - VisibleWidth(branchTag));
                if (VisibleWidth(display) > maxDisplay) display = TruncateToWidth(display, maxDisplay - 3) + "...";
                display += branchTag;

                var rowPlain = $"  {icon} {age,6} {size,6} {display}";
                var rowMarkup = $"  {icon} {age,6} {size,6} {Markup.Escape(display)}";
                int rowVis = VisibleWidth(rowPlain);
                if (rowVis < listWidth) rowMarkup += new string(' ', listWidth - rowVis);
                else if (rowVis > listWidth)
                {
                    rowPlain = TruncateToWidth(rowPlain, listWidth);
                    rowMarkup = Markup.Escape(rowPlain);
                }

                bool isCursor = vi == cursorIdx;
                if (isCursor)
                {
                    try { AnsiConsole.Markup($"[invert]{rowMarkup}[/]"); }
                    catch { Console.Write(rowPlain); }
                }
                else
                {
                    try { AnsiConsole.Markup(rowMarkup); }
                    catch { Console.Write(rowPlain); }
                }
            }
            else
            {
                Console.Write(new string(' ', listWidth));
            }

            // Right side: preview panel
            if (showPreview)
            {
                int previewRow = vi - scrollTop + previewScroll;
                if (previewRow >= 0 && previewRow < previewLines.Count)
                {
                    var pLine = previewLines[previewRow];
                    var pVisible = StripMarkup(pLine);
                    pVisible = GetVisibleText(pVisible);
                    int pVisWidth = VisibleWidth(pVisible);
                    if (pVisWidth >= previewWidth)
                    {
                        var truncated = TruncateMarkupToWidth(pLine, previewWidth - 1);
                        try { AnsiConsole.Markup(truncated); }
                        catch { Console.Write(TruncateToWidth(pVisible, previewWidth - 1) + "…"); }
                    }
                    else
                    {
                        int padding = previewWidth - pVisWidth;
                        try { AnsiConsole.Markup(pLine + new string(' ', padding)); }
                        catch { Console.Write(pVisible + new string(' ', padding)); }
                    }
                }
                else
                {
                    Console.Write(new string(' ', previewWidth));
                }
            }
        }

        // Status bar
        AnsiConsole.Cursor.SetPosition(0, vh + 2);
        string statusText;
        if (inSearch)
            statusText = $" Filter: {searchBuf}_";
        else
        {
            var previewHint = showPreview ? "i close preview" : "i preview";
            statusText = $" ↑↓ navigate | Enter open | r resume | / filter | {previewHint} | q quit";
        }
        var escapedStatus = Markup.Escape(statusText);
        int statusVis = VisibleWidth(statusText);
        if (statusVis < w) escapedStatus += new string(' ', w - statusVis);
        try { AnsiConsole.Markup($"[invert]{escapedStatus}[/]"); }
        catch { Console.Write(statusText); }

        Console.CursorVisible = inSearch;
    }

    Console.CursorVisible = false;
    AnsiConsole.Clear();
    RebuildFiltered();
    Render();

    DateTime lastBrowserRender = DateTime.MinValue;
    while (true)
    {
        if (!Console.KeyAvailable)
        {
            // Check if new sessions arrived — throttle re-renders to max 4/sec
            int count;
            lock (sessionsLock) { count = allSessions.Count; }
            var now = DateTime.UtcNow;
            if (count != lastRenderedCount && (now - lastBrowserRender).TotalMilliseconds >= 250)
            {
                lastRenderedCount = count;
                lastBrowserRender = now;
                RebuildFiltered();
                ClampCursor();
                Render();
            }
            Thread.Sleep(50);
            continue;
        }

        var key = Console.ReadKey(true);

        if (inSearch)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                inSearch = false;
                searchBuf.Clear();
                searchFilter = "";
                RebuildFiltered();
                cursorIdx = 0;
                scrollTop = 0;
                Render();
                continue;
            }
            if (key.Key == ConsoleKey.Enter)
            {
                inSearch = false;
                searchFilter = searchBuf.ToString();
                searchBuf.Clear();
                RebuildFiltered();
                cursorIdx = 0;
                scrollTop = 0;
                ClampCursor();
                Render();
                continue;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (searchBuf.Length > 0) searchBuf.Remove(searchBuf.Length - 1, 1);
                searchFilter = searchBuf.ToString();
                RebuildFiltered();
                cursorIdx = 0;
                scrollTop = 0;
                ClampCursor();
                Render();
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                searchBuf.Append(key.KeyChar);
                searchFilter = searchBuf.ToString();
                RebuildFiltered();
                cursorIdx = 0;
                scrollTop = 0;
                ClampCursor();
                Render();
                continue;
            }
            continue;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                cursorIdx = Math.Max(0, cursorIdx - 1);
                ClampCursor();
                Render();
                break;
            case ConsoleKey.DownArrow:
                cursorIdx = Math.Min(filtered.Count - 1, cursorIdx + 1);
                ClampCursor();
                Render();
                break;
            case ConsoleKey.PageUp:
                cursorIdx = Math.Max(0, cursorIdx - ViewHeight());
                ClampCursor();
                Render();
                break;
            case ConsoleKey.PageDown:
                cursorIdx = Math.Min(filtered.Count - 1, cursorIdx + ViewHeight());
                ClampCursor();
                Render();
                break;
            case ConsoleKey.Home:
                cursorIdx = 0;
                ClampCursor();
                Render();
                break;
            case ConsoleKey.End:
                cursorIdx = Math.Max(0, filtered.Count - 1);
                ClampCursor();
                Render();
                break;
            case ConsoleKey.Enter:
                if (filtered.Count > 0 && cursorIdx < filtered.Count)
                {
                    string path;
                    lock (sessionsLock) { path = allSessions[filtered[cursorIdx]].eventsPath; }
                    Console.CursorVisible = true;
                    AnsiConsole.Clear();
                    return path;
                }
                break;
            case ConsoleKey.Escape:
                if (searchFilter.Length > 0)
                {
                    searchFilter = "";
                    RebuildFiltered();
                    cursorIdx = 0;
                    scrollTop = 0;
                    ClampCursor();
                    Render();
                }
                else
                {
                    Console.CursorVisible = true;
                    AnsiConsole.Clear();
                    return null;
                }
                break;
            default:
                switch (key.KeyChar)
                {
                    case 'k':
                        cursorIdx = Math.Max(0, cursorIdx - 1);
                        ClampCursor();
                        Render();
                        break;
                    case 'j':
                        cursorIdx = Math.Min(filtered.Count - 1, cursorIdx + 1);
                        ClampCursor();
                        Render();
                        break;
                    case '/':
                        inSearch = true;
                        searchBuf.Clear();
                        Render();
                        break;
                    case 'q':
                        Console.CursorVisible = true;
                        AnsiConsole.Clear();
                        return null;
                    case 'g':
                        cursorIdx = 0;
                        ClampCursor();
                        Render();
                        break;
                    case 'G':
                        cursorIdx = Math.Max(0, filtered.Count - 1);
                        ClampCursor();
                        Render();
                        break;
                    case 'i':
                        showPreview = !showPreview;
                        if (showPreview)
                        {
                            previewSessionId = null;
                            LoadPreview();
                        }
                        AnsiConsole.Clear();
                        Render();
                        break;
                    case '{':
                        if (showPreview)
                        {
                            previewScroll = Math.Max(0, previewScroll - ViewHeight() / 2);
                            Render();
                        }
                        break;
                    case '}':
                        if (showPreview)
                        {
                            previewScroll = Math.Min(Math.Max(0, previewLines.Count - ViewHeight()), previewScroll + ViewHeight() / 2);
                            Render();
                        }
                        break;
                    case 'r':
                        if (filtered.Count > 0 && cursorIdx >= 0 && cursorIdx < filtered.Count)
                        {
                            string rPath;
                            lock (sessionsLock) { rPath = allSessions[filtered[cursorIdx]].eventsPath; }
                            Console.CursorVisible = true;
                            AnsiConsole.Clear();
                            LaunchResume(rPath);
                            return null;
                        }
                        break;
                }
                break;
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


