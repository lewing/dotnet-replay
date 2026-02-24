using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using static TextUtils;
using static EvalProcessor;

/// <summary>
/// Interactive terminal pager with scrolling, search, filtering, and follow mode.
/// </summary>
class InteractivePager(ColorHelper colors, ContentRenderer cr, bool noColor, string? filePath, string? filterType, bool expandTools, int? tail)
{
    public PagerAction Run<T>(List<string> headerLines, List<string> contentLines, T parsedData, bool isJsonlFormat, bool noFollow)
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
}
