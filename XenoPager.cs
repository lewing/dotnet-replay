using System.Globalization;
using System.Text;
using System.Text.Json;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Input;
using static TextUtils;
using static EvalProcessor;

/// <summary>
/// Interactive terminal pager built on XenoAtom.Terminal.UI using LogControl.
/// </summary>
class XenoPager(ContentRenderer cr, bool noColor, string? filePath, string? filterType, bool expandTools)
{
    public PagerAction Run<T>(List<string> headerLines, List<string> contentLines, T parsedData, bool isJsonlFormat, bool noFollow)
    {
        // State
        string? currentFilter = filterType;
        bool currentExpandTools = expandTools;
        bool showInfoOverlay = false;
        bool following = isJsonlFormat && !noFollow;
        long lastFileOffset = 0;
        int fileChangedFlag = 0;
        DateTime lastReadTime = DateTime.MinValue;
        FileSystemWatcher? watcher = null;

        PagerAction result = PagerAction.Quit;
        bool exitRequested = false;
        bool needsRepopulate = false;

        // Filter setup
        string[] filterCycle = ["all", "user", "assistant", "tool", "error"];
        int filterIndex = currentFilter is null ? 0 : Array.IndexOf(filterCycle, currentFilter);
        if (filterIndex < 0) filterIndex = 0;

        // Build info bar text
        string infoBar = parsedData switch
        {
            JsonlData jData => StripMarkup(cr.BuildJsonlInfoBar(jData)) + (following ? " ↓ FOLLOWING" : ""),
            EvalData eData => StripMarkup(cr.BuildEvalInfoBar(eData)) + (following ? " ↓ FOLLOWING" : ""),
            WazaData wData => StripMarkup(cr.BuildWazaInfoBar(wData, filePath ?? "")),
            _ => $"[{Path.GetFileName(filePath)}]"
        };

        // === UI Elements ===
        var log = new LogControl()
            .WrapText(false)
            .IsSelectable(true)
            .AutoFocus(true);

        // Populate log with content lines
        void PopulateLog()
        {
            log.Clear();
            var lines = showInfoOverlay ? headerLines : contentLines;
            foreach (var line in lines)
                log.AppendLine(StripMarkup(line));
        }
        PopulateLog();

        void RebuildContent()
        {
            var filter = filterIndex == 0 ? null : filterCycle[filterIndex];
            contentLines = parsedData switch
            {
                JsonlData jd => cr.RenderJsonlContentLines(jd, filter, currentExpandTools),
                EvalData ed => cr.RenderEvalContentLines(ed, filter, currentExpandTools),
                WazaData wd => cr.RenderWazaContentLines(wd, filter, currentExpandTools),
                _ => contentLines
            };
        }

        // Top info bar
        var header = new Header()
            .Left(new TextBlock(() =>
            {
                var statusFilter = filterIndex == 0 ? "all" : filterCycle[filterIndex];
                var followIndicator = following ? " LIVE" : "";
                return $"{infoBar} | Filter: {statusFilter}{followIndicator}";
            }));

        // Info overlay panel (hidden by default)
        var infoBlocks = new TextBlock[headerLines.Count];
        for (int i = 0; i < headerLines.Count; i++)
            infoBlocks[i] = new TextBlock(StripMarkup(headerLines[i]));
        var infoPanel = new ScrollViewer(new VStack(infoBlocks))
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        infoPanel.IsVisible = false;

        // Content area: ZStack layers both at full size, toggle visibility
        var content = new ZStack(log, infoPanel);

        // Layout
        var root = new DockLayout()
            .Top(header)
            .Bottom(new CommandBar())
            .Content(content);

        // === Commands ===
        log.AddCommand(new Command
        {
            Id = "Pager.Quit",
            LabelMarkup = "Quit",
            Gesture = new KeyGesture('q'),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                result = PagerAction.Quit;
                exitRequested = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Escape",
            LabelMarkup = "Escape",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ =>
            {
                result = PagerAction.Quit;
                exitRequested = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Browse",
            LabelMarkup = "Browse",
            Gesture = new KeyGesture('b'),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                result = PagerAction.Browse;
                exitRequested = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Resume",
            LabelMarkup = "Resume",
            Gesture = new KeyGesture('r'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                result = PagerAction.Resume;
                exitRequested = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.ToggleTools",
            LabelMarkup = "Toggle Tools",
            Gesture = new KeyGesture('t'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                currentExpandTools = !currentExpandTools;
                RebuildContent();
                needsRepopulate = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.CycleFilter",
            LabelMarkup = "Filter",
            Gesture = new KeyGesture('f'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                filterIndex = (filterIndex + 1) % filterCycle.Length;
                RebuildContent();
                needsRepopulate = true;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Info",
            LabelMarkup = "Info",
            Gesture = new KeyGesture('i'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                showInfoOverlay = !showInfoOverlay;
                log.IsVisible = !showInfoOverlay;
                infoPanel.IsVisible = showInfoOverlay;
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Search",
            LabelMarkup = "Search",
            Gesture = new KeyGesture('/'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => log.OpenSearch()
        });

        // === Follow mode watcher ===
        if (following && filePath is not null)
        {
            lastFileOffset = new FileInfo(filePath).Length;
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
            var name = Path.GetFileName(filePath);
            watcher = new FileSystemWatcher(dir, name);
            watcher.Changed += (_, _) => Interlocked.Exchange(ref fileChangedFlag, 1);
            watcher.EnableRaisingEvents = true;
        }

        // === Run the terminal UI ===
        try
        {
            using var session = Terminal.Open();
            Terminal.Run(root, () =>
            {
                if (exitRequested)
                    return TerminalLoopResult.Stop;

                // Repopulate after filter/tools change
                if (needsRepopulate)
                {
                    PopulateLog();
                    needsRepopulate = false;
                }

                // Follow mode: check for file changes
                if (following && Interlocked.CompareExchange(ref fileChangedFlag, 0, 1) == 1 && filePath is not null)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastReadTime).TotalMilliseconds >= 100)
                    {
                        lastReadTime = now;
                        try
                        {
                            var fi = new FileInfo(filePath);
                            if (fi.Length > lastFileOffset)
                                AppendFollowData();
                        }
                        catch { /* ignore read errors */ }
                    }
                }

                return TerminalLoopResult.Continue;
            });
        }
        finally
        {
            watcher?.Dispose();
        }

        return result;

        // === Follow mode: append new data from file ===
        void AppendFollowData()
        {
            List<string> newLines = [];
            using (var fs = new FileStream(filePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(lastFileOffset, SeekOrigin.Begin);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) is not null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        newLines.Add(line);
                }
                lastFileOffset = fs.Position;
            }

            if (newLines.Count == 0) return;

            if (parsedData is JsonlData jdFollow)
            {
                foreach (var rawLine in newLines)
                {
                    try
                    {
                        var doc = JsonDocument.Parse(rawLine);
                        jdFollow.Events.Add(doc);
                        var evRoot = doc.RootElement;
                        var evType = SafeGetString(evRoot, "type");
                        var tsStr = SafeGetString(evRoot, "timestamp");
                        DateTimeOffset? ts = null;
                        if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                            ts = dto;
                        if (evType is "user.message" or "assistant.message" or "tool.execution_start" or "tool.result")
                            jdFollow.Turns.Add((evType, evRoot, ts));
                        jdFollow.EventCount = jdFollow.Events.Count;
                    }
                    catch { }
                }
                RebuildContent();
                infoBar = StripMarkup(cr.BuildJsonlInfoBar(jdFollow)) + " ↓ FOLLOWING";
                // Append only new rendered lines to log
                needsRepopulate = true;
            }
            else if (parsedData is EvalData edFollow)
            {
                foreach (var rawLine in newLines)
                    ProcessEvalEvent(edFollow, rawLine);
                RebuildContent();
                infoBar = StripMarkup(cr.BuildEvalInfoBar(edFollow)) + " ↓ FOLLOWING";
                needsRepopulate = true;
            }
        }
    }
}
