using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using Microsoft.Data.Sqlite;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;
var markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
var JsonlSerializerOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
};
var SummarySerializerOptions = new JsonSerializerOptions
{
    WriteIndented = true
};

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
            OutputStatsReport(new List<FileStats>(), groupBy, statsJson, failThreshold);
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
        var stats = ExtractStats(file);
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
    OutputStatsReport(allStats, groupBy, statsJson, failThreshold);
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
string Blue(string s) => noColor ? s : $"[blue]{Markup.Escape(s)}[/]";
string Green(string s) => noColor ? s : $"[green]{Markup.Escape(s)}[/]";
string Yellow(string s) => noColor ? s : $"[yellow]{Markup.Escape(s)}[/]";
string Red(string s) => noColor ? s : $"[red]{Markup.Escape(s)}[/]";
string Dim(string s) => noColor ? s : $"[dim]{Markup.Escape(s)}[/]";
string Bold(string s) => noColor ? s : $"[bold]{Markup.Escape(s)}[/]";
string Cyan(string s) => noColor ? s : $"[cyan]{Markup.Escape(s)}[/]";
string Separator()
{
    int width = 80;
    try { width = AnsiConsole.Profile.Width; } catch { }
    return Dim(new string('─', Math.Max(1, width - 1)));
}

// --- Markdown rendering via Markdig ---

List<string> RenderMarkdownLines(string markdown, string colorName, string prefix = "┃ ")
{
    if (noColor || string.IsNullOrEmpty(markdown))
        return SplitLines(markdown).Select(l => $"{prefix}{l}").ToList();

    var doc = Markdown.Parse(markdown, markdownPipeline);
    List<string> result = [];

    foreach (var block in doc)
    {
        RenderBlock(block, result, colorName, prefix, 0);
    }

    // Filter consecutive blank separator lines (keep at most one between content)
    List<string> filtered = [];
    bool lastWasBlank = false;
    var blankPattern = noColor ? prefix.TrimEnd() : $"[{colorName}]{prefix.TrimEnd()}[/]";
    
    foreach (var line in result)
    {
        bool isBlank = line.Trim() == blankPattern.Trim() || GetVisibleText(line).Trim() == prefix.TrimEnd();
        if (isBlank && lastWasBlank)
            continue; // Skip consecutive blanks
        filtered.Add(line);
        lastWasBlank = isBlank;
    }

    return filtered;
}

void RenderBlock(Block block, List<string> lines, string colorName, string prefix, int depth)
{
    switch (block)
    {
        case HeadingBlock heading:
        {
            var text = RenderInlines(heading.Inline);
            var marker = Markup.Escape(new string('#', heading.Level) + " ");
            // Use compound tag to avoid nesting
            lines.Add(noColor ? $"{prefix}{marker}{text}" : $"[{colorName} bold]{prefix}{marker}{text}[/]");
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        case ParagraphBlock para:
        {
            var text = RenderInlines(para.Inline);
            foreach (var line in SplitLines(text))
            {
                // Text from RenderInlines may contain inline markup - wrap at line level
                if (noColor)
                    lines.Add($"{prefix}{line}");
                else
                    lines.Add($"[{colorName}]{prefix}{line}[/]");
            }
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        case FencedCodeBlock fenced:
        {
            var lang = fenced.Info ?? "";
            // Use compound tag for dim styling
            lines.Add(noColor ? $"{prefix}```{Markup.Escape(lang)}" : $"[{colorName} dim]{prefix}```{Markup.Escape(lang)}[/]");
            var codeLines = fenced.Lines;
            for (int i = 0; i < codeLines.Count; i++)
            {
                var line = codeLines.Lines[i].ToString();
                lines.Add(noColor ? $"{prefix}  {Markup.Escape(line)}" : $"[{colorName} dim]{prefix}  {Markup.Escape(line)}[/]");
            }
            lines.Add(noColor ? $"{prefix}```" : $"[{colorName} dim]{prefix}```[/]");
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        case CodeBlock code:
        {
            var codeLines = code.Lines;
            for (int i = 0; i < codeLines.Count; i++)
            {
                var line = codeLines.Lines[i].ToString();
                lines.Add(noColor ? $"{prefix}  {Markup.Escape(line)}" : $"[{colorName} dim]{prefix}  {Markup.Escape(line)}[/]");
            }
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        case ListBlock list:
        {
            int itemNum = 0;
            foreach (var item in list)
            {
                if (item is ListItemBlock listItem)
                {
                    itemNum++;
                    var bullet = list.IsOrdered ? $"{itemNum}. " : "• ";
                    var indent = new string(' ', depth * 2);
                    bool first = true;
                    foreach (var sub in listItem)
                    {
                        if (first && sub is ParagraphBlock p)
                        {
                            var text = RenderInlines(p.Inline);
                            foreach (var (line, idx) in SplitLines(text).Select((l, i) => (l, i)))
                            {
                                if (idx == 0)
                                {
                                    if (noColor)
                                        lines.Add($"{prefix}{indent}{Markup.Escape(bullet)}{line}");
                                    else
                                        lines.Add($"[{colorName}]{prefix}{indent}{Markup.Escape(bullet)}{line}[/]");
                                }
                                else
                                {
                                    if (noColor)
                                        lines.Add($"{prefix}{indent}{new string(' ', bullet.Length)}{line}");
                                    else
                                        lines.Add($"[{colorName}]{prefix}{indent}{new string(' ', bullet.Length)}{line}[/]");
                                }
                            }
                            first = false;
                        }
                        else
                        {
                            RenderBlock(sub, lines, colorName, prefix + indent + new string(' ', bullet.Length), depth + 1);
                            first = false;
                        }
                    }
                }
            }
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        case ThematicBreakBlock:
        {
            lines.Add(noColor ? $"{prefix}───" : $"[{colorName} dim]{prefix}───[/]");
            break;
        }
        case QuoteBlock quote:
        {
            foreach (var sub in quote)
            {
                // Quote prefix should not contain markup - handle dim styling in the block rendering
                var quotePrefix = prefix + "▎ ";
                RenderBlock(sub, lines, colorName, quotePrefix, depth);
            }
            break;
        }
        case Markdig.Extensions.Tables.Table table:
        {
            // Collect all rows and their cell texts
            var rows = new List<List<string>>();
            foreach (var rowBlock in table)
            {
                if (rowBlock is Markdig.Extensions.Tables.TableRow row)
                {
                    var cells = new List<string>();
                    foreach (var cellBlock in row)
                    {
                        if (cellBlock is Markdig.Extensions.Tables.TableCell cell)
                        {
                            var cellText = new StringBuilder();
                            foreach (var sub in cell)
                            {
                                if (sub is ParagraphBlock p)
                                    cellText.Append(RenderInlines(p.Inline));
                            }
                            cells.Add(cellText.ToString());
                        }
                    }
                    if (cells.Count > 0)
                        rows.Add(cells);
                }
            }
            if (rows.Count > 0)
            {
                // Compute column widths
                int colCount = rows.Max(r => r.Count);
                var widths = new int[colCount];
                foreach (var row in rows)
                    for (int c = 0; c < row.Count; c++)
                        widths[c] = Math.Max(widths[c], StripMarkup(row[c]).Length);

                for (int r = 0; r < rows.Count; r++)
                {
                    var sb = new StringBuilder();
                    for (int c = 0; c < colCount; c++)
                    {
                        if (c > 0) sb.Append(" | ");
                        var cell = c < rows[r].Count ? rows[r][c] : "";
                        var pad = widths[c] - StripMarkup(cell).Length;
                        sb.Append(cell);
                        if (pad > 0) sb.Append(new string(' ', pad));
                    }
                    var line = sb.ToString();
                    if (noColor)
                        lines.Add($"{prefix}{line}");
                    else
                        lines.Add($"[{colorName}]{prefix}{line}[/]");

                    // Add separator after header row
                    if (r == 0)
                    {
                        var sep = new StringBuilder();
                        for (int c = 0; c < colCount; c++)
                        {
                            if (c > 0) sep.Append(" | ");
                            sep.Append(new string('-', Math.Max(widths[c], 3)));
                        }
                        var sepLine = sep.ToString();
                        if (noColor)
                            lines.Add($"{prefix}{sepLine}");
                        else
                            lines.Add($"[{colorName} dim]{prefix}{sepLine}[/]");
                    }
                }
            }
            lines.Add(noColor ? prefix : $"[{colorName}]{prefix}[/]");
            break;
        }
        default:
        {
            // Fallback: render raw text for unknown block types
            var rawLines = block.ToString() is string s ? SplitLines(s) : [];
            foreach (var line in rawLines)
            {
                if (noColor)
                    lines.Add($"{prefix}{Markup.Escape(line)}");
                else
                    lines.Add($"[{colorName}]{prefix}{Markup.Escape(line)}[/]");
            }
            break;
        }
    }
}

string RenderInlines(ContainerInline? container)
{
    if (container == null) return "";
    var sb = new StringBuilder();
    foreach (var inline in container)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(Markup.Escape(lit.Content.ToString()));
                break;
            case EmphasisInline em:
                var inner = RenderInlines(em);
                if (em.DelimiterCount >= 2)
                    sb.Append(noColor ? inner : $"[bold]{inner}[/]");
                else
                    sb.Append(noColor ? inner : $"[italic]{inner}[/]");
                break;
            case CodeInline code:
                sb.Append(noColor ? code.Content : $"[cyan]{Markup.Escape(code.Content)}[/]");
                break;
            case LinkInline link:
                var linkText = RenderInlines(link);
                var url = link.Url ?? "";
                if (string.IsNullOrEmpty(linkText)) linkText = Markup.Escape(url);
                sb.Append(noColor ? $"{linkText} ({url})" : $"[underline blue]{linkText}[/] [dim]({Markup.Escape(url)})[/]");
                break;
            case LineBreakInline:
                sb.Append('\n');
                break;
            case HtmlInline html:
                sb.Append(Markup.Escape(html.Tag));
                break;
            default:
                sb.Append(Markup.Escape(inline.ToString() ?? ""));
                break;
        }
    }
    return sb.ToString();
}

string StripMarkup(string s)
{
    // Preserve escaped brackets
    s = s.Replace("[[", "\x01").Replace("]]", "\x02");
    // Strip all markup tags
    s = Regex.Replace(s, @"\[[^\[\]]*\]", "");
    // Restore escaped brackets to their visible form
    s = s.Replace("\x01", "[").Replace("\x02", "]");
    return s;
}

string GetVisibleText(string s) => StripMarkup(s);

string[] SplitLines(string s) => s.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

static bool IsWideEmojiInBMP(int value) => value switch
{
    0x231A or 0x231B => true, // ⌚⌛
    0x23E9 or 0x23EA or 0x23EB or 0x23EC or 0x23F0 or 0x23F3 => true,
    >= 0x25AA and <= 0x25AB => true,
    0x25B6 or 0x25C0 => true,
    >= 0x25FB and <= 0x25FE => true,
    >= 0x2600 and <= 0x2604 => true,
    0x260E or 0x2611 => true,
    >= 0x2614 and <= 0x2615 => true,
    0x2618 or 0x261D or 0x2620 => true,
    >= 0x2622 and <= 0x2623 => true,
    0x2626 or 0x262A or 0x262E or 0x262F => true,
    >= 0x2638 and <= 0x263A => true,
    0x2640 or 0x2642 => true,
    >= 0x2648 and <= 0x2653 => true, // zodiac
    0x265F or 0x2660 or 0x2663 or 0x2665 or 0x2666 => true,
    0x2668 or 0x267B or 0x267E or 0x267F => true,
    >= 0x2692 and <= 0x2697 => true,
    0x2699 or 0x269B or 0x269C => true,
    >= 0x26A0 and <= 0x26A1 => true,
    >= 0x26AA and <= 0x26AB => true,
    >= 0x26B0 and <= 0x26B1 => true,
    >= 0x26BD and <= 0x26BE => true,
    >= 0x26C4 and <= 0x26C5 => true,
    0x26C8 or 0x26CE or 0x26CF => true,
    0x26D1 or 0x26D3 or 0x26D4 => true,
    0x26E9 or 0x26EA => true,
    >= 0x26F0 and <= 0x26F5 => true,
    >= 0x26F7 and <= 0x26FA => true,
    0x26FD => true,
    0x2702 or 0x2705 => true,
    >= 0x2708 and <= 0x270D => true,
    0x270F => true,
    0x2712 or 0x2714 or 0x2716 => true,
    0x271D or 0x2721 => true,
    0x2728 => true,
    0x2733 or 0x2734 => true,
    0x2744 or 0x2747 => true,
    0x274C or 0x274E => true,
    >= 0x2753 and <= 0x2755 => true,
    0x2757 => true,
    >= 0x2763 and <= 0x2764 => true,
    >= 0x2795 and <= 0x2797 => true,
    0x27A1 or 0x27B0 or 0x27BF => true,
    >= 0x2934 and <= 0x2935 => true,
    >= 0x2B05 and <= 0x2B07 => true,
    0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
    _ => false
};

int RuneWidth(Rune rune)
{
    int v = rune.Value;
    // Zero-width: variation selectors and combining marks
    if (v == 0xFE0F || v == 0xFE0E || (v >= 0x200B && v <= 0x200F) || v == 0x2060 || v == 0xFEFF)
        return 0;
    // Wide: CJK, fullwidth, emoji
    if (v >= 0x1100 && (
        (v <= 0x115F) ||                          // Hangul Jamo
        (v >= 0x2E80 && v <= 0x9FFF) ||            // CJK
        (v >= 0xF900 && v <= 0xFAFF) ||            // CJK Compatibility
        (v >= 0xFE30 && v <= 0xFE6F) ||            // CJK Compatibility Forms
        (v >= 0xFF01 && v <= 0xFF60) ||             // Fullwidth forms
        (v >= 0x1F000)))                            // Supplementary emoji (🔧💭 etc.)
        return 2;
    // BMP emoji with default emoji presentation
    if (IsWideEmojiInBMP(v))
        return 2;
    return 1;
}

int VisibleWidth(string s)
{
    int width = 0;
    foreach (var rune in s.EnumerateRunes())
    {
        width += RuneWidth(rune);
    }
    return width;
}

string TruncateToWidth(string s, int maxWidth)
{
    int width = 0;
    int i = 0;
    foreach (var rune in s.EnumerateRunes())
    {
        int charWidth = RuneWidth(rune);
        if (width + charWidth > maxWidth) break;
        width += charWidth;
        i += rune.Utf16SequenceLength;
    }
    return s[..i];
}

string TruncateMarkupToWidth(string markupText, int maxWidth)
{
    var result = new StringBuilder();
    var openTags = new Stack<string>();
    int visWidth = 0;
    int i = 0;
    while (i < markupText.Length && visWidth < maxWidth)
    {
        // Check for escaped brackets [[ or ]]
        if (i + 1 < markupText.Length && markupText[i] == '[' && markupText[i + 1] == '[')
        {
            if (visWidth + 1 > maxWidth) break;
            result.Append("[[");
            visWidth++;
            i += 2;
            continue;
        }
        if (i + 1 < markupText.Length && markupText[i] == ']' && markupText[i + 1] == ']')
        {
            if (visWidth + 1 > maxWidth) break;
            result.Append("]]");
            visWidth++;
            i += 2;
            continue;
        }
        // Check for markup tags [xxx] or [/xxx] or [/]
        if (markupText[i] == '[')
        {
            int closeIdx = markupText.IndexOf(']', i + 1);
            if (closeIdx > i)
            {
                var tag = markupText[(i + 1)..closeIdx];
                result.Append(markupText[i..(closeIdx + 1)]);
                if (tag == "/" || tag.StartsWith("/"))
                {
                    if (openTags.Count > 0) openTags.Pop();
                }
                else
                {
                    openTags.Push(tag);
                }
                i = closeIdx + 1;
                continue;
            }
        }
        // Regular character — check width
        try
        {
            var rune = Rune.GetRuneAt(markupText, i);
            int charWidth = RuneWidth(rune);
            if (visWidth + charWidth > maxWidth) break;
            result.Append(markupText.AsSpan(i, rune.Utf16SequenceLength));
            visWidth += charWidth;
            i += rune.Utf16SequenceLength;
        }
        catch
        {
            // Invalid surrogate pair — treat as single-width character
            if (visWidth + 1 > maxWidth) break;
            result.Append(markupText[i]);
            visWidth++;
            i++;
        }
    }
    // Append ellipsis inside the current markup context if truncated, then close open tags
    bool wasTruncated = i < markupText.Length;
    if (wasTruncated) result.Append('…');
    while (openTags.Count > 0)
    {
        openTags.Pop();
        result.Append("[/]");
    }
    return result.ToString();
}

string SkipMarkupWidth(string markupText, int skipColumns)
{
    if (skipColumns <= 0) return markupText;
    List<string> openTags = [];
    int visWidth = 0;
    int i = 0;
    while (i < markupText.Length && visWidth < skipColumns)
    {
        // Escaped brackets [[ or ]]
        if (i + 1 < markupText.Length && markupText[i] == '[' && markupText[i + 1] == '[')
        {
            visWidth++;
            i += 2;
            continue;
        }
        if (i + 1 < markupText.Length && markupText[i] == ']' && markupText[i + 1] == ']')
        {
            visWidth++;
            i += 2;
            continue;
        }
        // Markup tags
        if (markupText[i] == '[')
        {
            int closeIdx = markupText.IndexOf(']', i + 1);
            if (closeIdx > i)
            {
                var tag = markupText[(i + 1)..closeIdx];
                if (tag == "/" || tag.StartsWith("/"))
                {
                    if (openTags.Count > 0) openTags.RemoveAt(openTags.Count - 1);
                }
                else
                {
                    openTags.Add(tag);
                }
                i = closeIdx + 1;
                continue;
            }
        }
        // Regular character
        try
        {
            var rune = Rune.GetRuneAt(markupText, i);
            int charWidth = RuneWidth(rune);
            visWidth += charWidth;
            i += rune.Utf16SequenceLength;
        }
        catch
        {
            visWidth++;
            i++;
        }
    }
    // Re-open any tags that were active at the skip point
    var prefix = new StringBuilder();
    foreach (var tag in openTags)
        prefix.Append($"[{tag}]");
    return prefix.ToString() + markupText[i..];
}

string PadVisible(string s, int totalWidth)
{
    var visible = GetVisibleText(s);
    int padding = totalWidth - VisibleWidth(visible);
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
                var parsed = isClaude ? ParseClaudeData(path) : ParseJsonlData(path);
                if (parsed is not null)
                    OutputSummary(parsed, jsonMode);
            }
            else if (isWaza)
            {
                var parsed = ParseWazaData(wazaDoc!);
                OutputWazaSummary(parsed, jsonMode);
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
                var parsed = isClaude ? ParseClaudeData(path) : ParseJsonlData(path);
                if (parsed is not null)
                    OutputJsonl(parsed, filterType, expandTools);
            }
            else if (isWaza)
            {
                var parsed = ParseWazaData(wazaDoc!);
                OutputWazaJsonl(parsed, filterType, expandTools);
            }
        }
        else
        {
            // Standard stream mode
            if (isEval) StreamEvalEvents(path);
            else if (isJsonl && !isClaude) StreamEventsJsonl(path);
            else if (isJsonl && isClaude) { var p = ParseClaudeData(path); if (p != null) { var cl = RenderJsonlContentLines(p, filterType, expandTools); foreach (var l in cl) Console.WriteLine(l); } }
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
            headerLines = RenderEvalHeaderLines(parsed);
            contentLines = RenderEvalContentLines(parsed, filterType, expandTools);
            return RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: true, noFollow: noFollow);
        }
        else if (isJsonl)
        {
            var parsed = isClaude ? ParseClaudeData(path) : ParseJsonlData(path);
            if (parsed is null) return PagerAction.Quit;
            headerLines = RenderJsonlHeaderLines(parsed);
            contentLines = RenderJsonlContentLines(parsed, filterType, expandTools);
            return RunInteractivePager(headerLines, contentLines, parsed, isJsonlFormat: !isClaude, noFollow: isClaude || noFollow);
        }
        else if (isWaza)
        {
            var parsed = ParseWazaData(wazaDoc!);
            headerLines = RenderWazaHeaderLines(parsed);
            contentLines = RenderWazaContentLines(parsed, filterType, expandTools);
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

// ========== JSONL Parser ==========
JsonlData? ParseJsonlData(string path)
{
    List<JsonDocument> events = [];
    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        try { events.Add(JsonDocument.Parse(line)); }
        catch { /* skip malformed lines */ }
    }
    if (events.Count == 0) { Console.Error.WriteLine(Bold("No events found")); return null; }

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

// ========== Claude Code Parser ==========
bool IsClaudeFormat(string path)
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

bool IsEvalFormat(string path)
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

EvalData? ParseEvalData(string path)
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

void ProcessEvalEvent(EvalData eval, string line)
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

JsonlData? ParseClaudeData(string path)
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

// ========== Waza Parser ==========
WazaData ParseWazaData(JsonDocument doc)
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

// ========== Info bar builders (compact 1-line header) ==========
string BuildJsonlInfoBar(JsonlData d)
{
    List<string> parts = [];
    if (d.SessionId != "") parts.Add($"session {d.SessionId}");
    if (d.CopilotVersion != "") parts.Add(d.CopilotVersion);
    parts.Add($"{d.EventCount} events");
    return $"[{string.Join(" | ", parts)}]";
}

string BuildWazaInfoBar(WazaData d)
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

// ========== Full header builders (for 'i' overlay) ==========
List<string> RenderJsonlHeaderLines(JsonlData d)
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

string BuildEvalInfoBar(EvalData d)
{
    var passed = d.TotalPassed > 0 ? $"✅{d.TotalPassed}" : "";
    var failed = d.TotalFailed > 0 ? $" ❌{d.TotalFailed}" : "";
    var skipped = d.TotalSkipped > 0 ? $" ⏭{d.TotalSkipped}" : "";
    var inProgress = d.CurrentCase is not null ? $" ⏳{d.CurrentCase}" : "";
    var dur = d.TotalDurationMs > 0 ? $" {d.TotalDurationMs / 1000.0:F1}s" : "";
    return $" {d.Suite} {passed}{failed}{skipped}{inProgress}{dur}";
}

List<string> RenderEvalHeaderLines(EvalData d)
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

List<string> RenderEvalContentLines(EvalData d, string? filter, bool expandTool)
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
        Console.WriteLine(JsonSerializer.Serialize(summary, SummarySerializerOptions));
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

List<string> RenderWazaHeaderLines(WazaData d)
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
List<string> RenderJsonlContentLines(JsonlData d, string? filter, bool expandTool)
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
                    foreach (var line in RenderMarkdownLines(content, "green"))
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
                // Enrich tool display with context from arguments
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
                        foreach (var line in SplitLines(truncated).Take(full ? int.MaxValue : 20))
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
    List<string> lines = [];
    if (d.TranscriptItems.Length == 0) { lines.Add(Dim("  No events found")); return lines; }

    // Pre-scan: build tool_call_id → tool_name map from execution_start events
    var toolCallNames = new Dictionary<string, string>();
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
    // Track message index to alternate user/assistant for "message" type events
    int messageIndex = 0;
    foreach (var item in d.TranscriptItems)
    {
        var itemType = SafeGetString(item, "type").ToLowerInvariant();
        var content = SafeGetString(item, "content");
        if (string.IsNullOrEmpty(content)) content = SafeGetString(item, "message");
        var toolName = SafeGetString(item, "tool_name");
        // Resolve tool_name from pre-scan map if missing (e.g. tool.execution_complete events)
        if (string.IsNullOrEmpty(toolName))
        {
            var callId = SafeGetString(item, "tool_call_id");
            if (!string.IsNullOrEmpty(callId) && toolCallNames.TryGetValue(callId, out var mapped))
                toolName = mapped;
        }

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

        // Skip partial_result heartbeat events — zero payload, pure noise
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
            foreach (var line in RenderMarkdownLines(content, "green"))
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
                        var resLines = SplitLines(truncated).Take(full ? int.MaxValue : 20).ToArray();
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
        infoBar = BuildJsonlInfoBar(jData) + (following ? " ↓ FOLLOWING" : "");
    else if (parsedData is EvalData eData)
        infoBar = BuildEvalInfoBar(eData) + (following ? " ↓ FOLLOWING" : "");
    else if (parsedData is WazaData wData)
        infoBar = BuildWazaInfoBar(wData);
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
            contentLines = RenderJsonlContentLines(jd, filter, currentExpandTools);
        else if (parsedData is EvalData ed)
            contentLines = RenderEvalContentLines(ed, filter, currentExpandTools);
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
                                infoBar = BuildJsonlInfoBar(jdFollow) + " ↓ FOLLOWING";
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
                                infoBar = BuildEvalInfoBar(edFollow) + " ↓ FOLLOWING";
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
                        headerLines = RenderJsonlHeaderLines(jdResize);
                    else if (parsedData is EvalData edResize)
                        headerLines = RenderEvalHeaderLines(edResize);
                    else if (parsedData is WazaData wdResize)
                        headerLines = RenderWazaHeaderLines(wdResize);
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
    var parsed = ParseJsonlData(path);
    if (parsed is null) return;
    var d = parsed;

    // Header card
    foreach (var line in RenderJsonlHeaderLines(d))
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
    foreach (var line in RenderJsonlContentLines(d, filterType, expandTools))
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
    var d = ParseWazaData(doc);
    foreach (var line in RenderWazaHeaderLines(d))
    {
        if (noColor)
            Console.WriteLine(StripMarkup(line));
        else
        {
            try { AnsiConsole.MarkupLine(line); }
            catch { Console.WriteLine(StripMarkup(line)); }
        }
    }
    foreach (var line in RenderWazaContentLines(d, filterType, expandTools))
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
            // Fallback: scan filesystem for Copilot sessions (only when not using external DB)
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
            var data = IsClaudeFormat(eventsPath) ? ParseClaudeData(eventsPath) : ParseJsonlData(eventsPath);
            if (data == null) { previewLines = ["", "  (unable to load preview)"]; return; }
            if (data.Turns.Count > 50)
                data = data with { Turns = data.Turns.Skip(data.Turns.Count - 50).ToList() };
            previewLines = RenderJsonlContentLines(data, null, false);
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
                int maxDisplay = Math.Max(10, listWidth - 21 - branchTag.Length);
                if (display.Length > maxDisplay) display = display[..(maxDisplay - 3)] + "...";
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

string FormatAge(TimeSpan age)
{
    if (age.TotalMinutes < 1) return "now";
    if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m";
    if (age.TotalDays < 1) return $"{(int)age.TotalHours}h";
    if (age.TotalDays < 30) return $"{(int)age.TotalDays}d";
    return $"{(int)(age.TotalDays / 30)}mo";
}

string FormatFileSize(long bytes)
{
    if (bytes < 1024) return $"{bytes}B";
    if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
    return $"{bytes / (1024 * 1024.0):F1}MB";
}


// ========== JSON and Summary Output Modes ==========

void OutputJsonl(JsonlData d, string? filter, bool expandTool)
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
                Console.WriteLine(JsonSerializer.Serialize(json, JsonlSerializerOptions));
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
                Console.WriteLine(JsonSerializer.Serialize(json, JsonlSerializerOptions));
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
                Console.WriteLine(JsonSerializer.Serialize(json, JsonlSerializerOptions));
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
                    Console.WriteLine(JsonSerializer.Serialize(json, JsonlSerializerOptions));
                }
                break;
            }
        }
    }
}

void OutputWazaJsonl(WazaData d, string? filter, bool expandTool)
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
            Console.WriteLine(JsonSerializer.Serialize(json, JsonlSerializerOptions));
            turnIndex++;
        }
    }
}

void OutputSummary(JsonlData d, bool asJson)
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
        Console.WriteLine(JsonSerializer.Serialize(json, SummarySerializerOptions));
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

void OutputWazaSummary(WazaData d, bool asJson)
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
        Console.WriteLine(JsonSerializer.Serialize(json, SummarySerializerOptions));
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

string FormatDuration(TimeSpan ts)
{
    if (ts.TotalSeconds < 60)
        return $"{(int)ts.TotalSeconds}s";
    if (ts.TotalMinutes < 60)
        return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
    return $"{(int)ts.TotalHours}h {ts.Minutes}m";
}

List<string> ExpandGlob(string pattern)
{
    var result = new List<string>();
    
    // Check if pattern contains wildcards
    if (!pattern.Contains('*') && !pattern.Contains('?'))
    {
        // No wildcards, treat as literal path
        if (File.Exists(pattern))
            result.Add(Path.GetFullPath(pattern));
        return result;
    }
    
    // Split pattern into directory and filename parts
    var dirPath = Path.GetDirectoryName(pattern);
    var fileName = Path.GetFileName(pattern);
    
    if (string.IsNullOrEmpty(dirPath))
        dirPath = ".";
    
    if (!Directory.Exists(dirPath))
        return result;
    
    try
    {
        var files = Directory.GetFiles(dirPath, fileName, SearchOption.TopDirectoryOnly);
        result.AddRange(files.Select(Path.GetFullPath));
    }
    catch
    {
        // Ignore errors (e.g., access denied)
    }
    
    return result;
}

FileStats? ExtractStats(string filePath)
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
                    var wazaData = ParseWazaData(wazaDoc);
                    
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
            var jsonlData = isClaude ? ParseClaudeData(filePath) : ParseJsonlData(filePath);
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

void OutputStatsReport(List<FileStats> stats, string? groupBy, bool asJson, int? failThreshold)
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
                
                Console.WriteLine(JsonSerializer.Serialize(new { summary, by_model = byModel }, SummarySerializerOptions));
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
                
                Console.WriteLine(JsonSerializer.Serialize(new { summary, by_task = byTask }, SummarySerializerOptions));
            }
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(summary, SummarySerializerOptions));
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


