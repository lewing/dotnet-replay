using System.Text.Json;
using Microsoft.Data.Sqlite;
using static TextUtils;

/// <summary>Static utility methods for session metadata enrichment and DB schema detection.</summary>
static class SessionUtils
{
    /// <summary>Detect DB type by inspecting schema tables.</summary>
    public static SessionDbType DetectDbType(string dbPath)
    {
        if (!File.Exists(dbPath)) return SessionDbType.Unknown;
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

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

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='schema_version'";
                if (cmd.ExecuteScalar() != null) return SessionDbType.CopilotCli;
            }

            return SessionDbType.Unknown;
        }
        catch { return SessionDbType.Unknown; }
    }

    internal static Dictionary<string, string> ReadWorkspaceProperties(string yamlPath)
    {
        Dictionary<string, string> props = [];
        if (!File.Exists(yamlPath)) return props;

        try
        {
            foreach (var line in File.ReadLines(yamlPath))
            {
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0) continue;
                var key = line[..colonIdx].Trim();
                var value = line[(colonIdx + 1)..].Trim().Trim('"');
                props[key] = value;
            }
        }
        catch { }

        return props;
    }

    internal static (string Branch, string Repository) EnrichCopilotSessionMetadata(
        string yamlPath,
        string eventsPath,
        string branch,
        string repository)
    {
        if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(repository))
        {
            var props = ReadWorkspaceProperties(yamlPath);
            if (string.IsNullOrEmpty(branch)) branch = props.GetValueOrDefault("branch", "");
            if (string.IsNullOrEmpty(repository)) repository = props.GetValueOrDefault("repository", "");
        }

        if ((!string.IsNullOrEmpty(branch) && !string.IsNullOrEmpty(repository)) || !File.Exists(eventsPath))
            return (branch, repository);

        try
        {
            foreach (var line in File.ReadLines(eventsPath).Take(10))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (SafeGetString(root, "type") == "session.start"
                    && root.TryGetProperty("data", out var data)
                    && data.TryGetProperty("context", out var context))
                {
                    if (string.IsNullOrEmpty(branch)) branch = SafeGetString(context, "branch");
                    if (string.IsNullOrEmpty(repository)) repository = SafeGetString(context, "repository");
                    if (!string.IsNullOrEmpty(branch) && !string.IsNullOrEmpty(repository))
                        break;
                }
            }
        }
        catch { }

        return (branch, repository);
    }

    internal static string ReadClaudeBranch(string eventsPath)
    {
        if (!File.Exists(eventsPath)) return "";

        try
        {
            foreach (var line in File.ReadLines(eventsPath).Take(10))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var branch = SafeGetString(doc.RootElement, "gitBranch");
                if (!string.IsNullOrEmpty(branch))
                    return branch;
            }
        }
        catch { }

        return "";
    }

    public static bool CanRun(string command)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(3000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }
}
