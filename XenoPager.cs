using System.Globalization;
using System.Reflection;
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
class XenoPager(ContentRenderer cr, string? filePath, string? filterType, bool expandTools)
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
        var logScrollViewerField = typeof(LogControl).GetField("_scrollViewer", BindingFlags.Instance | BindingFlags.NonPublic);

        ScrollViewer? GetLogScrollViewer() => logScrollViewerField?.GetValue(log) as ScrollViewer;

        // Track scroll offsets locally to avoid reading ScrollViewer offsets,
        // which conflicts with LogControl's internal write behavior during arrange
        // passes (XenoAtom binding tracking forbids read-then-write on the same
        // property within a single tracking context).
        int trackedOffset = 0;
        int trackedHorizontalOffset = 0;

        int MaxVerticalOffset()
        {
            var scrollViewer = GetLogScrollViewer();
            if (scrollViewer is null)
                return 0;
            return Math.Max(0, log.Count - scrollViewer.ViewportHeight);
        }

        int GetVerticalOffset() => trackedOffset;
        int GetHorizontalOffset() => trackedHorizontalOffset;

        void SetVerticalOffset(int offset)
        {
            var scrollViewer = GetLogScrollViewer();
            if (scrollViewer is null)
                return;
            trackedOffset = Math.Clamp(offset, 0, MaxVerticalOffset());
            scrollViewer.VerticalOffset = trackedOffset;
        }

        void SetHorizontalOffset(int offset)
        {
            var scrollViewer = GetLogScrollViewer();
            if (scrollViewer is null)
                return;
            trackedHorizontalOffset = Math.Max(0, offset);
            scrollViewer.HorizontalOffset = trackedHorizontalOffset;
        }

        int PageSize() => Math.Max(1, GetLogScrollViewer()?.ViewportHeight ?? 1);
        void ScrollByLines(int delta) => SetVerticalOffset(GetVerticalOffset() + delta);
        void ScrollByPages(int pages) => SetVerticalOffset(GetVerticalOffset() + (PageSize() * pages));
        void ScrollToTop() => SetVerticalOffset(0);
        void ScrollToBottom() => SetVerticalOffset(MaxVerticalOffset());

        // Populate log with content lines
        void PopulateLog()
        {
            var previousOffset = GetVerticalOffset();
            var restoreToBottom = previousOffset >= MaxVerticalOffset();

            log.Clear();
            var lines = showInfoOverlay ? headerLines : contentLines;
            foreach (var line in lines)
                log.AppendLine(StripMarkup(line));

            if (restoreToBottom)
                ScrollToBottom();
            else
                SetVerticalOffset(previousOffset);

            SetHorizontalOffset(GetHorizontalOffset());
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
                var totalLines = log.Count;
                var currentLine = totalLines == 0 ? 0 : Math.Min(totalLines, GetVerticalOffset() + 1);
                var colIndicator = trackedHorizontalOffset > 0 ? $" Col {trackedHorizontalOffset}+" : "";
                var searchInfo = log.MatchCount > 0 ? $" ({log.MatchCount} matches)" : "";
                var followIndicator = following ? " LIVE" : "";
                return $"Line {currentLine}/{totalLines}{colIndicator}{searchInfo} | Filter: {statusFilter}{followIndicator} | {infoBar}";
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

        log.AddCommand(new Command
        {
            Id = "Pager.PageUp",
            LabelMarkup = "Page Up",
            Gesture = new KeyGesture(TerminalKey.PageUp),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollByPages(-1)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.PageDown",
            LabelMarkup = "Page Down",
            Gesture = new KeyGesture(TerminalKey.PageDown),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollByPages(1)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.Home",
            LabelMarkup = "Top",
            Gesture = new KeyGesture(TerminalKey.Home),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollToTop()
        });

        log.AddCommand(new Command
        {
            Id = "Pager.End",
            LabelMarkup = "Bottom",
            Gesture = new KeyGesture(TerminalKey.End),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollToBottom()
        });

        log.AddCommand(new Command
        {
            Id = "Pager.LineDown",
            LabelMarkup = "Line Down",
            Gesture = new KeyGesture('j'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollByLines(1)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.LineUp",
            LabelMarkup = "Line Up",
            Gesture = new KeyGesture('k'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollByLines(-1)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.GoTop",
            LabelMarkup = "Top",
            Gesture = new KeyGesture('g'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollToTop()
        });

        log.AddCommand(new Command
        {
            Id = "Pager.GoBottom",
            LabelMarkup = "Bottom",
            Gesture = new KeyGesture('G'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => ScrollToBottom()
        });

        log.AddCommand(new Command
        {
            Id = "Pager.ScrollLeft",
            LabelMarkup = "Left",
            Gesture = new KeyGesture('h'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => SetHorizontalOffset(GetHorizontalOffset() - 8)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.ScrollRight",
            LabelMarkup = "Right",
            Gesture = new KeyGesture('l'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => SetHorizontalOffset(GetHorizontalOffset() + 8)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.ScrollColumnStart",
            LabelMarkup = "Column 0",
            Gesture = new KeyGesture('0'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => SetHorizontalOffset(0)
        });

        log.AddCommand(new Command
        {
            Id = "Pager.SearchNext",
            LabelMarkup = "Next Match",
            Gesture = new KeyGesture('n'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ =>
            {
                if (log.MatchCount > 0)
                    log.GoToNextMatch();
            }
        });

        log.AddCommand(new Command
        {
            Id = "Pager.SearchPrevious",
            LabelMarkup = "Previous Match",
            Gesture = new KeyGesture('N'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ =>
            {
                if (log.MatchCount > 0)
                    log.GoToPreviousMatch();
            }
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
            Console.ResetColor();
            Console.CursorVisible = true;
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
                        using var doc = JsonDocument.Parse(rawLine);
                        var evRoot = doc.RootElement;
                        var evType = SafeGetString(evRoot, "type");
                        var tsStr = SafeGetString(evRoot, "timestamp");
                        DateTimeOffset? ts = null;
                        if (DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                            ts = dto;
                        if (evType is "user.message" or "assistant.message" or "tool.execution_start" or "tool.result")
                            // Keep only the cloned turn payload so follow mode can render without retaining every JsonDocument.
                            jdFollow.Turns.Add((evType, evRoot.Clone(), ts));
                        jdFollow.EventCount++;
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
