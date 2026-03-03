using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Commands;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.DataGrid;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;
using XenoAtom.Terminal.UI.Styling;
using static TextUtils;
using DotnetReplay;

namespace DotnetReplay
{
    /// <summary>Bindable row model for the session DataGrid.</summary>
    sealed partial class SessionRow
    {
        public SessionRow()
        {
            Icon = "";
            Age = "";
            Size = "";
            Summary = "";
            Branch = "";
        }

        [Bindable] public partial string Icon { get; set; }
        [Bindable] public partial string Age { get; set; }
        [Bindable] public partial string Size { get; set; }
        [Bindable] public partial string Summary { get; set; }
        [Bindable] public partial string Branch { get; set; }
        // Non-bindable metadata
        public string EventsPath { get; set; } = "";
        public string SessionId { get; set; } = "";
        public BrowserSession? Source { get; set; }
    }
}

/// <summary>
/// Session browser built on XenoAtom.Terminal.UI with DataGrid, search, and preview.
/// </summary>
class XenoSessionBrowser(ContentRenderer cr, DataParsers dataParsers, string? sessionStateDir)
{
    SessionDbType _currentDbType = SessionDbType.CopilotCli;

    /// <summary>
    /// Browse sessions using XenoAtom.Terminal.UI fullscreen DataGrid.
    /// Returns the selected events.jsonl path, or null if the user quit.
    /// </summary>
    public string? BrowseSessions(string? dbPathOverride = null)
    {
        if (dbPathOverride == null && !Directory.Exists(sessionStateDir))
        {
            Console.Error.WriteLine("No Copilot session directory found.");
            Console.Error.WriteLine($"Expected: {sessionStateDir}");
            return null;
        }

        // Shared state between background scan and UI
        List<BrowserSession> allSessions = [];
        var sessionsLock = new System.Threading.Lock();
        bool scanComplete = false;
        bool isSkillDb = false;

        // Result tracking
        string? selectedPath = null;
        bool resumeRequested = false;

        // Observable state
        var sessionCount = new State<int>(0);
        var showPreview = new State<bool>(false);
        bool exitRequested = false;

        // DataGrid
        var doc = new DataGridListDocument<SessionRow>();
        using (doc.BeginUpdate())
        {
            doc.AddColumn(new DataGridColumnInfo<string>("icon", " ", ReadOnly: true, SessionRow.Accessor.Icon));
            doc.AddColumn(new DataGridColumnInfo<string>("age", "Age", ReadOnly: true, SessionRow.Accessor.Age));
            doc.AddColumn(new DataGridColumnInfo<string>("size", "Size", ReadOnly: true, SessionRow.Accessor.Size));
            doc.AddColumn(new DataGridColumnInfo<string>("summary", "Summary", ReadOnly: true, SessionRow.Accessor.Summary));
            doc.AddColumn(new DataGridColumnInfo<string>("branch", "Branch", ReadOnly: true, SessionRow.Accessor.Branch));
        }

        using var view = new DataGridDocumentView(doc);
        var grid = new DataGridControl { View = view }
            .ShowHeader(true)
            .ShowRowAnchor(true)
            .RowAnchorWidth(0)
            .SelectionMode(DataGridSelectionMode.Row)
            .ReadOnly(true)
            .FrozenColumns(1);

        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "icon",
            TypedValueAccessor = SessionRow.Accessor.Icon,
            Width = GridLength.Fixed(3),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "age",
            TypedValueAccessor = SessionRow.Accessor.Age,
            Width = GridLength.Fixed(8),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "size",
            TypedValueAccessor = SessionRow.Accessor.Size,
            Width = GridLength.Fixed(8),
            CellAlignment = TextAlignment.Right,
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "summary",
            TypedValueAccessor = SessionRow.Accessor.Summary,
            Width = GridLength.Star(3),
        });
        grid.Columns.Add(new DataGridColumn<string>
        {
            Key = "branch",
            TypedValueAccessor = SessionRow.Accessor.Branch,
            Width = GridLength.Star(1),
        });

        // Preview panel — VStack of TextBlocks, one per line (TextBlock ignores \n)
        var previewStack = new VStack()
            .HorizontalAlignment(Align.Stretch);
        var previewScroll = new ScrollViewer(previewStack)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        int lastSelectedRow = -1;

        // Layout
        var header = new Header()
            .Left(new TextBlock(() =>
            {
                var label = isSkillDb ? "🧪 Skill Eval" : "📋 Sessions";
                var loading = scanComplete ? "" : " Loading...";
                return $"{label} — {sessionCount.Value} sessions{loading}";
            }));

        var gridScroll = new ScrollViewer(grid)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var previewBorder = new Border(previewScroll)
            .Style(BorderStyle.Single)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
        previewBorder.MaxWidth = 60;
        previewBorder.IsVisible = false;

        var content = new HStack(
                gridScroll,
                previewBorder)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);

        var root = new DockLayout()
            .Top(header)
            .Bottom(new CommandBar())
            .Content(content);

        var toastHost = new ToastHost();
        toastHost.Content(root);

        // Helper to get the currently selected SessionRow
        SessionRow? GetSelectedRow()
        {
            var rowIdx = grid.CurrentCell.Row;
            if (rowIdx < 0 || rowIdx >= doc.Rows.Count) return null;

            // When the view has sorting/filtering, we need to get the row model
            // through the snapshot which maps visual indices to document indices
            var snapshot = view.CurrentSnapshot;
            if (snapshot is null || rowIdx >= snapshot.RowCount) return null;
            return snapshot.GetRowModel(rowIdx) as SessionRow;
        }

        // Commands — registered on grid (checked first in parent walk from focused element)
        Action openAction = () =>
        {
            var row = GetSelectedRow();
            if (row?.EventsPath is not null && File.Exists(row.EventsPath))
            {
                selectedPath = row.EventsPath;
                exitRequested = true;
            }
        };
        Action quitAction = () => exitRequested = true;

        grid.AddCommand(new Command
        {
            Id = "Browser.Open",
            LabelMarkup = "Open",
            Gesture = new KeyGesture(TerminalKey.Enter),
            Importance = CommandImportance.Primary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => openAction()
        });

        grid.AddCommand(new Command
        {
            Id = "Browser.Quit",
            LabelMarkup = "Quit",
            Gesture = new KeyGesture(TerminalKey.Escape),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => quitAction()
        });

        toastHost.AddCommand(new Command
        {
            Id = "Browser.QuitQ",
            LabelMarkup = "Quit",
            Gesture = new KeyGesture('q'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.None,
            Execute = _ => quitAction()
        });

        toastHost.AddCommand(new Command
        {
            Id = "Browser.Search",
            LabelMarkup = "Search",
            Gesture = new KeyGesture(TerminalChar.CtrlF, TerminalModifiers.Ctrl),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ => grid.FilterRowVisible = !grid.FilterRowVisible
        });

        toastHost.AddCommand(new Command
        {
            Id = "Browser.Preview",
            LabelMarkup = "Preview",
            Gesture = new KeyGesture('i'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                showPreview.Value = !showPreview.Value;
                previewBorder.IsVisible = showPreview.Value;
                if (showPreview.Value) lastSelectedRow = -1; // force refresh
            }
        });

        toastHost.AddCommand(new Command
        {
            Id = "Browser.Resume",
            LabelMarkup = "Resume",
            Gesture = new KeyGesture('r'),
            Importance = CommandImportance.Secondary,
            Presentation = CommandPresentation.CommandBar,
            Execute = _ =>
            {
                var row = GetSelectedRow();
                if (row?.EventsPath is not null)
                {
                    selectedPath = row.EventsPath;
                    resumeRequested = true;
                    exitRequested = true;
                }
            }
        });

        // Background session loading
        var scanThread = new Thread(() => LoadAllSessions(
            dbPathOverride, allSessions, sessionsLock,
            ref scanComplete, ref isSkillDb,
            (sessions) =>
            {
                using (doc.BeginUpdate())
                {
                    foreach (var s in sessions)
                        doc.AddRow(MakeRow(s));
                }
                sessionCount.Value = doc.Rows.Count;
            }));
        scanThread.IsBackground = true;
        scanThread.Start();

        // Run the fullscreen UI
        using var session = Terminal.Open();
        Terminal.Run(toastHost, () =>
        {
            if (exitRequested)
                return TerminalLoopResult.Stop;

            // Update preview when selection changes or preview just toggled on
            if (showPreview.Value)
            {
                var currentRow = grid.CurrentCell.Row;
                if (currentRow != lastSelectedRow)
                {
                    lastSelectedRow = currentRow;
                    var row = GetSelectedRow();
                    var text = row?.Source is not null ? BuildPreviewText(row.Source) : "";
                    previewStack.Children.Clear();
                    foreach (var line in text.Split('\n'))
                        previewStack.Children.Add(new TextBlock(line));
                }
            }
            return TerminalLoopResult.Continue;
        });

        if (resumeRequested && selectedPath is not null)
        {
            LaunchResume(selectedPath);
            return null;
        }

        return selectedPath;
    }

    static SessionRow MakeRow(BrowserSession s)
    {
        string icon;
        if (s.DbType == SessionDbType.SkillValidator)
        {
            icon = s.Status switch
            {
                "completed" => "✅",
                "timed_out" => "⏱️",
                "running" => "🔄",
                _ => "🧪"
            };
        }
        else
            icon = s.EventsPath.Contains(".claude") ? "🔴" : "🤖";

        var age = FormatAge(DateTime.UtcNow - s.UpdatedAt);
        var size = FormatFileSize(s.FileSize);
        var summary = s.DbType == SessionDbType.SkillValidator && !string.IsNullOrEmpty(s.Summary)
            ? $"{s.Repository}: {s.Summary}"
            : (!string.IsNullOrEmpty(s.Summary) ? s.Summary.ReplaceLineEndings(" ") : s.Cwd);
        var branch = !string.IsNullOrEmpty(s.Branch) ? s.Branch : "";

        return new SessionRow
        {
            Icon = icon,
            Age = age,
            Size = size,
            Summary = summary,
            Branch = branch,
            EventsPath = s.EventsPath,
            SessionId = s.Id,
            Source = s
        };
    }

    string BuildPreviewText(BrowserSession s)
    {
        var sb = new StringBuilder();
        if (s.DbType == SessionDbType.SkillValidator)
        {
            sb.AppendLine($"Skill: {s.SkillName}");
            sb.AppendLine($"Scenario: {s.ScenarioName}");
            sb.AppendLine($"Role: {s.Role}");
            sb.AppendLine($"Model: {s.Model}");
            sb.AppendLine($"Status: {s.Status}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(s.Prompt))
            {
                sb.AppendLine("Prompt:");
                foreach (var line in s.Prompt.Split('\n').Take(10))
                    sb.AppendLine($"  {line.TrimEnd()}");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(s.MetricsJson))
            {
                sb.AppendLine("Metrics:");
                try
                {
                    var jsonDoc = JsonDocument.Parse(s.MetricsJson);
                    foreach (var prop in jsonDoc.RootElement.EnumerateObject().Take(10))
                        sb.AppendLine($"  {prop.Name}: {prop.Value}");
                }
                catch { sb.AppendLine($"  {s.MetricsJson[..Math.Min(200, s.MetricsJson.Length)]}"); }
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine($"Session: {s.Id}");
            sb.AppendLine($"Directory: {s.Cwd}");
            if (!string.IsNullOrEmpty(s.Branch)) sb.AppendLine($"Branch: {s.Branch}");
            if (!string.IsNullOrEmpty(s.Repository)) sb.AppendLine($"Repository: {s.Repository}");
            sb.AppendLine($"Updated: {s.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Size: {FormatFileSize(s.FileSize)}");
            sb.AppendLine();

            // Read first user messages from the events file
            if (File.Exists(s.EventsPath))
            {
                try
                {
                    int msgCount = 0;
                    foreach (var line in File.ReadLines(s.EventsPath).Take(50))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var doc = JsonDocument.Parse(line);
                            var root = doc.RootElement;
                            var role = SafeGetString(root, "role");
                            if (role == "user")
                            {
                                var content = SafeGetString(root, "content");
                                if (string.IsNullOrEmpty(content) && root.TryGetProperty("content", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in cArr.EnumerateArray())
                                    {
                                        if (SafeGetString(item, "type") == "text")
                                        { content = SafeGetString(item, "text"); break; }
                                    }
                                }
                                if (!string.IsNullOrEmpty(content))
                                {
                                    msgCount++;
                                    var preview = content.ReplaceLineEndings(" ");
                                    if (preview.Length > 120) preview = preview[..117] + "...";
                                    sb.AppendLine($"User: {preview}");
                                    if (msgCount >= 3) break;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        return sb.ToString();
    }

    void LoadAllSessions(
        string? dbPathOverride,
        List<BrowserSession> allSessions,
        System.Threading.Lock sessionsLock,
        ref bool scanComplete,
        ref bool isSkillDb,
        Action<List<BrowserSession>> onNewSessions)
    {
        var dbSessions = LoadSessionsFromDb(sessionStateDir!, dbPathOverride);
        HashSet<string> knownSessionIds = [];

        if (dbSessions is not null)
        {
            isSkillDb = _currentDbType == SessionDbType.SkillValidator;
            lock (sessionsLock)
            {
                allSessions.AddRange(dbSessions);
                allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                foreach (var s in dbSessions) knownSessionIds.Add(s.Id);
            }
            onNewSessions(dbSessions);
        }
        else if (dbPathOverride == null)
        {
            var batch = new List<BrowserSession>();
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

                var session = new BrowserSession(id, summary, cwd, updatedAt, eventsPath, fileSize, "", "");
                lock (sessionsLock)
                {
                    allSessions.Add(session);
                    knownSessionIds.Add(id);
                }
                batch.Add(session);

                if (batch.Count >= 50)
                {
                    lock (sessionsLock) allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    onNewSessions(batch);
                    batch = [];
                }
            }
            if (batch.Count > 0)
            {
                lock (sessionsLock) allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                onNewSessions(batch);
            }
        }

        // Claude Code sessions
        if (dbPathOverride == null)
        {
            var claudeProjectsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            if (Directory.Exists(claudeProjectsDir))
            {
                var batch = new List<BrowserSession>();
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
                                var lineDoc = JsonDocument.Parse(line);
                                var root = lineDoc.RootElement;
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
                            var session = new BrowserSession(claudeId, claudeSummary, claudeCwd, claudeUpdatedAt, jsonlFile, fileSize, "", "");
                            lock (sessionsLock)
                            {
                                allSessions.Add(session);
                                knownSessionIds.Add(claudeId);
                            }
                            batch.Add(session);
                        }
                        catch { continue; }
                    }
                }
                if (batch.Count > 0)
                {
                    lock (sessionsLock) allSessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    onNewSessions(batch);
                }
            }
        }

        scanComplete = true;
    }

    // DB loading methods (reuse schema detection from SessionBrowser)
    List<BrowserSession>? LoadSessionsFromDb(string sessionStateDir, string? dbPathOverride = null)
    {
        var dbPath = dbPathOverride ?? Path.Combine(Path.GetDirectoryName(sessionStateDir)!, "session-store.db");
        var dbType = SessionBrowser.DetectDbType(dbPath);

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
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
                var ver = cmd.ExecuteScalar();
                if (ver == null || Convert.ToInt32(ver) != 1) return null;
            }
            HashSet<string> expectedCols = ["id", "cwd", "summary", "updated_at", "branch", "repository"];
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(sessions)";
                HashSet<string> actualCols = [];
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) actualCols.Add(reader.GetString(1));
                if (!expectedCols.IsSubsetOf(actualCols)) return null;
            }
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
                        continue;
                    results.Add(new BrowserSession(id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
                }
            }
            return results;
        }
        catch { return null; }
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
                var eventsPath = "";
                long fileSize = 0;
                if (configDir is not null)
                {
                    var normalizedConfigDir = configDir.Replace('\\', '/');
                    var configFullPath = Path.Combine(dbDir, normalizedConfigDir);
                    eventsPath = Path.Combine(configFullPath, "events.jsonl");
                    if (!File.Exists(eventsPath))
                    {
                        var ssd = Path.Combine(configFullPath, "session-state");
                        if (Directory.Exists(ssd))
                        {
                            foreach (var subDir in Directory.GetDirectories(ssd))
                            {
                                var candidate = Path.Combine(subDir, "events.jsonl");
                                if (File.Exists(candidate)) { eventsPath = candidate; break; }
                            }
                        }
                    }
                    if (File.Exists(eventsPath))
                        try { fileSize = new FileInfo(eventsPath).Length; } catch { }
                }
                var summaryText = $"{scenarioName} ({role})";
                var runTag = runIndex > 0 ? $" #{runIndex}" : "";
                var cwd = workDir ?? skillPath;
                results.Add(new BrowserSession(
                    Id: id, Summary: summaryText + runTag, Cwd: cwd, UpdatedAt: updatedAt,
                    EventsPath: eventsPath, FileSize: fileSize, Branch: model, Repository: skillName,
                    DbType: SessionDbType.SkillValidator, SkillName: skillName, ScenarioName: scenarioName,
                    Role: role, Model: model, Status: status, Prompt: prompt,
                    MetricsJson: metricsJson, JudgeJson: judgeJson, PairwiseJson: pairwiseJson));
            }
            return results;
        }
        catch { return null; }
    }

    public void LaunchResume(string path)
    {
        bool isClaude = path.Contains(Path.Combine(".claude", "projects"));
        string? sessionId = isClaude
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(Path.GetDirectoryName(path));

        if (string.IsNullOrEmpty(sessionId))
        {
            Console.Error.WriteLine("Error: Could not determine session ID for resume.");
            return;
        }

        string command, args;
        if (isClaude)
        {
            command = "claude";
            args = $"--resume \"{sessionId}\"";
        }
        else if (SessionBrowser.CanRunStatic("copilot"))
        {
            command = "copilot";
            args = $"--resume \"{sessionId}\"";
        }
        else
        {
            command = "gh";
            args = $"copilot --resume \"{sessionId}\"";
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
        }
    }
}
