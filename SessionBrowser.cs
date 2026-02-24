using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using static TextUtils;
using static EvalProcessor;

class SessionBrowser(ColorHelper colors, ContentRenderer cr, DataParsers dataParsers, string? sessionStateDir)
{
    public List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)>? LoadSessionsFromDb(string sessionStateDir, string? dbPathOverride = null)
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

    public string? BrowseSessions(string? dbPathOverride = null)
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
            var dbSessions = LoadSessionsFromDb(sessionStateDir!, dbPathOverride);
            string? dbPath = null;
            var knownSessionIds = new HashSet<string>();
            DateTime lastUpdatedAt = DateTime.MinValue;

            if (dbSessions != null)
            {
                // Record the DB path for polling
                dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir!)!, "session-store.db");
                
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
                foreach (var dir in Directory.GetDirectories(sessionStateDir!))
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
                            
                            var eventsPath = Path.Combine(sessionStateDir!, id, "events.jsonl");
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
                previewLines = cr.RenderJsonlContentLines(data, null, false);
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

    public void LaunchResume(string path)
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
}
