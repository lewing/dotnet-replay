using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spectre.Console;
using static TextUtils;
using static EvalProcessor;

class SessionBrowser(ContentRenderer cr, DataParsers dataParsers, string? sessionStateDir)
{
    SessionDbType _currentDbType = SessionDbType.CopilotCli;

    /// <summary>Detect DB type by inspecting schema tables.</summary>
    public static SessionDbType DetectDbType(string dbPath)
    {
        if (!File.Exists(dbPath)) return SessionDbType.Unknown;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            // Check for skill-validator schema_info table
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM schema_info WHERE key='type' LIMIT 1";
                try
                {
                    var val = cmd.ExecuteScalar();
                    if (val is string s && s == "skill-validator")
                        return SessionDbType.SkillValidator;
                }
                catch { /* table doesn't exist */ }
            }

            // Check for Copilot CLI schema_version table
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
                if (cmd.ExecuteScalar() != null) return SessionDbType.CopilotCli;
            }

            return SessionDbType.Unknown;
        }
        catch { return SessionDbType.Unknown; }
    }

    public List<BrowserSession>? LoadSessionsFromDb(string sessionStateDir, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir)!, "session-store.db");
        var dbType = DetectDbType(dbPath);

        if (dbType == SessionDbType.SkillValidator)
        {
            _currentDbType = SessionDbType.SkillValidator;
            return LoadSkillValidatorSessions(dbPath);
        }

        if (dbType != SessionDbType.CopilotCli) return null;
        _currentDbType = SessionDbType.CopilotCli;
        return LoadCopilotCliSessions(sessionStateDir, dbPath);
    }

    List<BrowserSession>? LoadCopilotCliSessions(string sessionStateDir, string dbPath)
    {
        if (!File.Exists(dbPath)) return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            // Validate schema version
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
                var ver = cmd.ExecuteScalar();
                if (ver == null || Convert.ToInt32(ver) != 1) return null;
            }

            // Validate sessions table has expected columns
            HashSet<string> expectedCols = ["id", "cwd", "summary", "updated_at", "branch", "repository"];
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(sessions)";
                HashSet<string> actualCols = [];
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) actualCols.Add(reader.GetString(1));
                if (!expectedCols.IsSubsetOf(actualCols)) return null;
            }

            // Load sessions
            var results = new List<BrowserSession>();
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

                    results.Add(new BrowserSession(id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
                }
            }
            return results;
        }
        catch
        {
            return null;
        }
    }

    List<BrowserSession>? LoadSkillValidatorSessions(string dbPath)
    {
        if (!File.Exists(dbPath)) return null;
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            var results = new List<BrowserSession>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.id, s.skill_name, s.skill_path, s.scenario_name, s.run_index, s.role, s.model,
                       s.config_dir, s.work_dir, s.prompt, s.status, s.started_at, s.completed_at,
                       r.metrics_json, r.judge_json, r.pairwise_json
                FROM sessions s
                LEFT JOIN run_results r ON s.id = r.session_id
                ORDER BY s.skill_name, s.scenario_name, s.run_index, s.role
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var skillName = reader.GetString(1);
                var skillPath = reader.GetString(2);
                var scenarioName = reader.GetString(3);
                var runIndex = reader.GetInt32(4);
                var role = reader.GetString(5);
                var model = reader.GetString(6);
                var configDir = reader.IsDBNull(7) ? null : reader.GetString(7);
                var workDir = reader.IsDBNull(8) ? null : reader.GetString(8);
                var prompt = reader.IsDBNull(9) ? null : reader.GetString(9);
                var status = reader.GetString(10);
                var startedAtStr = reader.GetString(11);
                var completedAtStr = reader.IsDBNull(12) ? null : reader.GetString(12);
                var metricsJson = reader.IsDBNull(13) ? null : reader.GetString(13);
                var judgeJson = reader.IsDBNull(14) ? null : reader.GetString(14);
                var pairwiseJson = reader.IsDBNull(15) ? null : reader.GetString(15);

                var dateStr = completedAtStr ?? startedAtStr;
                DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt);

                // Resolve events.jsonl: config_dir is relative to DB directory
                // Normalize path separators for cross-platform (Windows backslashes → forward slashes)
                var eventsPath = "";
                long fileSize = 0;
                if (configDir is not null)
                {
                    var normalizedConfigDir = configDir.Replace('\\', '/');
                    var configFullPath = Path.Combine(dbDir, normalizedConfigDir);

                    // Try direct: config_dir/events.jsonl
                    eventsPath = Path.Combine(configFullPath, "events.jsonl");
                    if (!File.Exists(eventsPath))
                    {
                        // Try nested: config_dir/session-state/*/events.jsonl
                        var sessionStateDir2 = Path.Combine(configFullPath, "session-state");
                        if (Directory.Exists(sessionStateDir2))
                        {
                            foreach (var subDir in Directory.GetDirectories(sessionStateDir2))
                            {
                                var candidate = Path.Combine(subDir, "events.jsonl");
                                if (File.Exists(candidate)) { eventsPath = candidate; break; }
                            }
                        }
                    }
                    if (File.Exists(eventsPath))
                        try { fileSize = new FileInfo(eventsPath).Length; } catch { }
                }

                var summary = $"{scenarioName} ({role})";
                var runTag = runIndex > 0 ? $" #{runIndex}" : "";
                var cwd = workDir ?? skillPath;

                results.Add(new BrowserSession(
                    Id: id,
                    Summary: summary + runTag,
                    Cwd: cwd,
                    UpdatedAt: updatedAt,
                    EventsPath: eventsPath,
                    FileSize: fileSize,
                    Branch: model,
                    Repository: skillName,
                    DbType: SessionDbType.SkillValidator,
                    SkillName: skillName,
                    ScenarioName: scenarioName,
                    Role: role,
                    Model: model,
                    Status: status,
                    Prompt: prompt,
                    MetricsJson: metricsJson,
                    JudgeJson: judgeJson,
                    PairwiseJson: pairwiseJson));
            }
            return results;
        }
        catch
        {
            return null;
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

        List<BrowserSession> allSessions = [];
        var sessionsLock = new System.Threading.Lock();
        bool scanComplete = false;
        int lastRenderedCount = -1;
        bool isSkillDb = false;

        // Background scan thread — try DB first, fall back to file scan
        var scanThread = new Thread(() =>
        {
            // Try loading sessions from SQLite DB (fast path)
            var dbSessions = LoadSessionsFromDb(sessionStateDir!, dbPathOverride);
            string? dbPath = null;
            HashSet<string> knownSessionIds = [];
            DateTime lastUpdatedAt = DateTime.MinValue;

            if (dbSessions is not null)
            {
                // Record the DB path for polling
                dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir!)!, "session-store.db");
                isSkillDb = _currentDbType == SessionDbType.SkillValidator;
                
                lock (sessionsLock)
                {
                    allSessions.AddRange(dbSessions);
                    allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    foreach (var s in dbSessions)
                    {
                        knownSessionIds.Add(s.Id);
                        if (s.UpdatedAt > lastUpdatedAt) lastUpdatedAt = s.UpdatedAt;
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

                    Dictionary<string, string> props = [];
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
                        allSessions.Add(new BrowserSession(id, summary, cwd, updatedAt, eventsPath, fileSize, "", ""));
                        knownSessionIds.Add(id);
                        if (allSessions.Count % 50 == 0)
                            allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    }
                }
                lock (sessionsLock)
                {
                    allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
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
                                allSessions.Add(new BrowserSession(claudeId, claudeSummary, claudeCwd, claudeUpdatedAt, jsonlFile, fileSize, "", ""));
                                if (allSessions.Count % 50 == 0)
                                    allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                            }
                        }
                        catch { continue; }
                    }
                }
            }
            lock (sessionsLock)
            {
                allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
            }
            }
            scanComplete = true;
            
            // If we loaded from DB, poll for new sessions every 5 seconds
            if (dbPath is not null)
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
                        var newSessions = new List<BrowserSession>();
                        
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
                            
                            newSessions.Add(new BrowserSession(id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
                            knownSessionIds.Add(id);
                            if (updatedAt > lastUpdatedAt) lastUpdatedAt = updatedAt;
                        }
                        
                        if (newSessions.Count > 0)
                        {
                            lock (sessionsLock)
                            {
                                allSessions.AddRange(newSessions);
                                allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
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
                        var text = $"{s.Summary} {s.Cwd} {s.Id} {s.Branch} {s.Repository} {s.SkillName} {s.ScenarioName} {s.Role} {s.Model}";
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
            BrowserSession sess;
            lock (sessionsLock) { sess = allSessions[filtered[cursorIdx]]; }
            if (sess.Id == previewSessionId) return;
            previewSessionId = sess.Id;
            previewScroll = 0;
            try
            {
                if (sess.DbType == SessionDbType.SkillValidator)
                {
                    // Show eval metadata as preview for skill-validator sessions
                    previewLines = RenderSkillPreview(sess);
                    previewScroll = 0;
                    return;
                }

                var eventsPath = sess.EventsPath;
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

        List<string> RenderSkillPreview(BrowserSession s)
        {
            var lines = new List<string>
            {
                "",
                $"  [bold]Skill:[/] {Markup.Escape(s.SkillName ?? "")}",
                $"  [bold]Scenario:[/] {Markup.Escape(s.ScenarioName ?? "")}",
                $"  [bold]Role:[/] {Markup.Escape(s.Role ?? "")}",
                $"  [bold]Model:[/] {Markup.Escape(s.Model ?? "")}",
                $"  [bold]Status:[/] {Markup.Escape(s.Status ?? "")}",
                ""
            };

            if (!string.IsNullOrEmpty(s.Prompt))
            {
                lines.Add("  [bold]Prompt:[/]");
                var promptLines = s.Prompt.Split('\n');
                foreach (var pl in promptLines.Take(10))
                    lines.Add($"    {Markup.Escape(pl.TrimEnd())}");
                if (promptLines.Length > 10)
                    lines.Add($"    [dim]... ({promptLines.Length - 10} more lines)[/]");
                lines.Add("");
            }

            if (!string.IsNullOrEmpty(s.MetricsJson))
            {
                lines.Add("  [bold]Metrics:[/]");
                try
                {
                    var doc = JsonDocument.Parse(s.MetricsJson);
                    foreach (var prop in doc.RootElement.EnumerateObject().Take(10))
                        lines.Add($"    {Markup.Escape(prop.Name)}: {Markup.Escape(prop.Value.ToString())}");
                }
                catch { lines.Add($"    {Markup.Escape(s.MetricsJson[..Math.Min(200, s.MetricsJson.Length)])}"); }
                lines.Add("");
            }

            if (!string.IsNullOrEmpty(s.JudgeJson))
            {
                lines.Add("  [bold]Judge Result:[/]");
                try
                {
                    var doc = JsonDocument.Parse(s.JudgeJson);
                    foreach (var prop in doc.RootElement.EnumerateObject().Take(10))
                        lines.Add($"    {Markup.Escape(prop.Name)}: {Markup.Escape(prop.Value.ToString())}");
                }
                catch { lines.Add($"    {Markup.Escape(s.JudgeJson[..Math.Min(200, s.JudgeJson.Length)])}"); }
                lines.Add("");
            }

            if (!string.IsNullOrEmpty(s.PairwiseJson))
            {
                lines.Add("  [bold]Pairwise:[/]");
                try
                {
                    var doc = JsonDocument.Parse(s.PairwiseJson);
                    foreach (var prop in doc.RootElement.EnumerateObject().Take(10))
                        lines.Add($"    {Markup.Escape(prop.Name)}: {Markup.Escape(prop.Value.ToString())}");
                }
                catch { lines.Add($"    {Markup.Escape(s.PairwiseJson[..Math.Min(200, s.PairwiseJson.Length)])}"); }
                lines.Add("");
            }

            if (!string.IsNullOrEmpty(s.EventsPath) && File.Exists(s.EventsPath))
            {
                lines.Add($"  [dim]Events: {Markup.Escape(s.EventsPath)}[/]");
                lines.Add($"  [dim]Press Enter to view transcript[/]");
            }
            else
            {
                lines.Add("  [dim]No events.jsonl available[/]");
            }

            return lines;
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
                    var updated = cs.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    cursorInfo = $" | {cs.Id} {updated}";
                }
            }
            var headerLabel = isSkillDb ? "🧪 Skill Eval" : "📋 Sessions";
            var headerBase = $" {headerLabel} — {count} sessions{loadingStatus}{filterStatus}";
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
                    BrowserSession sess;
                    lock (sessionsLock)
                    {
                        var si = filtered[vi];
                        sess = allSessions[si];
                    }
                    var age = FormatAge(DateTime.UtcNow - sess.UpdatedAt);
                    var size = FormatFileSize(sess.FileSize);
                    string icon;
                    if (sess.DbType == SessionDbType.SkillValidator)
                    {
                        var statusIcon = sess.Status switch
                        {
                            "completed" => "✅",
                            "timed_out" => "⏱️",
                            "running" => "🔄",
                            _ => "🧪"
                        };
                        icon = statusIcon;
                    }
                    else
                        icon = sess.EventsPath.Contains(".claude") ? "🔴" : "🤖";
                    var branchTag = !string.IsNullOrEmpty(sess.Branch) ? $" [{sess.Branch}]" : "";
                    string display;
                    if (sess.DbType == SessionDbType.SkillValidator)
                        display = !string.IsNullOrEmpty(sess.Summary) ? $"{sess.Repository}: {sess.Summary}" : sess.Cwd;
                    else
                        display = !string.IsNullOrEmpty(sess.Summary) ? sess.Summary.ReplaceLineEndings(" ") : sess.Cwd;
                    int maxDisplay = Math.Max(10, listWidth - 19 - VisibleWidth(branchTag));
                    if (VisibleWidth(display) > maxDisplay) display = TruncateToWidth(display, maxDisplay - 3) + "...";
                    display += branchTag;

                    var rowPlain = $"  {icon} {age,6} {size,6} {display}";
                    var rowMarkup = $"  {icon} {age,6} {size,6} {Markup.Escape(display)}";
                    int rowVis = VisibleWidth(rowPlain);
                    if (rowVis > listWidth)
                    {
                        rowPlain = TruncateToWidth(rowPlain, listWidth - 1) + "…";
                        rowMarkup = Markup.Escape(rowPlain);
                        rowVis = VisibleWidth(rowPlain);
                    }
                    if (rowVis < listWidth) rowMarkup += new string(' ', listWidth - rowVis);

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
                    // Explicitly position cursor to prevent emoji-width drift
                    AnsiConsole.Cursor.SetPosition(listWidth, vi - scrollTop + 2);
                    int previewRow = vi - scrollTop + previewScroll;
                    if (previewRow >= 0 && previewRow < previewLines.Count)
                    {
                        var pLine = previewLines[previewRow];
                        var pVisible = StripMarkup(pLine);
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
                        lock (sessionsLock) { path = allSessions[filtered[cursorIdx]].EventsPath; }
                        if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        {
                            // No events file — toggle preview instead
                            if (!showPreview) { showPreview = true; previewSessionId = null; }
                            AnsiConsole.Clear();
                            Render();
                            break;
                        }
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
                                lock (sessionsLock) { rPath = allSessions[filtered[cursorIdx]].EventsPath; }
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
