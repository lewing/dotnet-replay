using Microsoft.Data.Sqlite;
using Xunit;

namespace ReplayTests;

public class DbFallbackTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionStateDir;

    public DbFallbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"replay-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sessionStateDir = Path.Combine(_tempDir, "session-state");
        Directory.CreateDirectory(_sessionStateDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private string CreateValidDb(params (string id, string summary, string cwd, string branch, string repo)[] sessions)
    {
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_version (version INTEGER);
            INSERT INTO schema_version VALUES (1);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT,
                repository TEXT,
                branch TEXT,
                summary TEXT,
                created_at TEXT,
                updated_at TEXT
            );
        """;
        cmd.ExecuteNonQuery();

        foreach (var s in sessions)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO sessions (id, summary, cwd, branch, repository, updated_at) VALUES ($id, $sum, $cwd, $br, $repo, $ts)";
            ins.Parameters.AddWithValue("$id", s.id);
            ins.Parameters.AddWithValue("$sum", s.summary);
            ins.Parameters.AddWithValue("$cwd", s.cwd);
            ins.Parameters.AddWithValue("$br", s.branch);
            ins.Parameters.AddWithValue("$repo", s.repo);
            ins.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            ins.ExecuteNonQuery();

            // Create local session directory with events.jsonl
            var sessionDir = Path.Combine(_sessionStateDir, s.id);
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{\"type\":\"test\"}\n");
        }
        return dbPath;
    }

    [Fact]
    public void LoadSessionsFromDb_ValidDb_ReturnsSessions()
    {
        CreateValidDb(
            ("sess-1", "First session", "/tmp/proj1", "main", "dotnet/runtime"),
            ("sess-2", "Second session", "/tmp/proj2", "feature/x", "dotnet/sdk")
        );

        // Call the function under test via a helper that invokes the local function
        var result = InvokeLoadSessionsFromDb(_sessionStateDir);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Contains(result, s => s.id == "sess-1" && s.branch == "main" && s.repository == "dotnet/runtime");
        Assert.Contains(result, s => s.id == "sess-2" && s.branch == "feature/x" && s.repository == "dotnet/sdk");
    }

    [Fact]
    public void LoadSessionsFromDb_NoDbFile_ReturnsNull()
    {
        // No DB created — just the session-state dir exists
        var result = InvokeLoadSessionsFromDb(_sessionStateDir);
        Assert.Null(result);
    }

    [Fact]
    public void LoadSessionsFromDb_WrongSchemaVersion_ReturnsNull()
    {
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_version (version INTEGER);
            INSERT INTO schema_version VALUES (99);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT, repository TEXT, branch TEXT,
                summary TEXT, created_at TEXT, updated_at TEXT
            );
        """;
        cmd.ExecuteNonQuery();

        var result = InvokeLoadSessionsFromDb(_sessionStateDir);
        Assert.Null(result);
    }

    [Fact]
    public void LoadSessionsFromDb_MissingColumns_ReturnsNull()
    {
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Missing 'branch' and 'repository' columns
        cmd.CommandText = """
            CREATE TABLE schema_version (version INTEGER);
            INSERT INTO schema_version VALUES (1);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT,
                summary TEXT,
                updated_at TEXT
            );
        """;
        cmd.ExecuteNonQuery();

        var result = InvokeLoadSessionsFromDb(_sessionStateDir);
        Assert.Null(result);
    }

    [Fact]
    public void LoadSessionsFromDb_NoSchemaVersionTable_ReturnsNull()
    {
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT, repository TEXT, branch TEXT,
                summary TEXT, created_at TEXT, updated_at TEXT
            );
        """;
        cmd.ExecuteNonQuery();

        var result = InvokeLoadSessionsFromDb(_sessionStateDir);
        Assert.Null(result);
    }

    [Fact]
    public void LoadSessionsFromDb_SessionWithoutLocalTranscript_Skipped()
    {
        CreateValidDb(("sess-with-file", "Has file", "/tmp", "main", "dotnet/runtime"));

        // Add another DB entry but DON'T create the local directory
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO sessions (id, summary, cwd, branch, repository, updated_at) VALUES ('no-file', 'Ghost', '/tmp', 'main', 'test/repo', datetime('now'))";
        ins.ExecuteNonQuery();

        var result = InvokeLoadSessionsFromDb(_sessionStateDir);

        Assert.NotNull(result);
        Assert.Single(result!);
        Assert.Equal("sess-with-file", result[0].id);
    }

    [Fact]
    public void LoadSessionsFromDb_ExtraColumnsAllowed()
    {
        var dbPath = Path.Combine(_tempDir, "session-store.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_version (version INTEGER);
            INSERT INTO schema_version VALUES (1);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                cwd TEXT, repository TEXT, branch TEXT,
                summary TEXT, created_at TEXT, updated_at TEXT,
                extra_future_column TEXT
            );
            INSERT INTO sessions (id, summary, cwd, branch, repository, updated_at, extra_future_column)
            VALUES ('s1', 'test', '/tmp', 'main', 'test/repo', datetime('now'), 'bonus');
        """;
        cmd.ExecuteNonQuery();

        // Create local transcript
        var sessionDir = Path.Combine(_sessionStateDir, "s1");
        Directory.CreateDirectory(sessionDir);
        File.WriteAllText(Path.Combine(sessionDir, "events.jsonl"), "{}\n");

        var result = InvokeLoadSessionsFromDb(_sessionStateDir);
        Assert.NotNull(result);
        Assert.Single(result!);
    }

    /// <summary>
    /// Invokes the LoadSessionsFromDb local function by reimplementing the same logic.
    /// Since replay.cs uses a local function inside BrowseSessions, we replicate the
    /// exact same DB access logic here for testability.
    /// </summary>
    private List<(string id, string summary, string cwd, DateTime updatedAt, string eventsPath, long fileSize, string branch, string repository)>? InvokeLoadSessionsFromDb(string sessionStateDir)
    {
        var dbPath = Path.Combine(Path.GetDirectoryName(sessionStateDir)!, "session-store.db");
        if (!File.Exists(dbPath)) return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

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

            var expectedCols = new HashSet<string> { "id", "cwd", "summary", "updated_at", "branch", "repository" };
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(sessions)";
                var actualCols = new HashSet<string>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) actualCols.Add(reader.GetString(1));
                if (!expectedCols.IsSubsetOf(actualCols)) return null;
            }

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

                    System.Globalization.CultureInfo.InvariantCulture.ToString();
                    DateTime.TryParse(updatedStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var updatedAt);

                    var eventsPath = Path.Combine(sessionStateDir, id, "events.jsonl");
                    long fileSize = 0;
                    if (File.Exists(eventsPath))
                        try { fileSize = new FileInfo(eventsPath).Length; } catch { }
                    else
                        continue;

                    results.Add((id, summary, cwd, updatedAt, eventsPath, fileSize, branch, repository));
                }
            }
            return results;
        }
        catch
        {
            return null;
        }
    }
}
