using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ReplayTests;

public class DbJsonOutputTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public DbJsonOutputTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"replay-db-json-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "sessions.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void DbFlag_SkillValidatorDbWithJson_EmitsSessionMetadata()
    {
        CreateSkillValidatorDb(withSession: true);

        var (stdout, stderr) = RunReplayWithArgs($"--db \"{_dbPath}\" --json");

        using var doc = JsonDocument.Parse(stdout);
        var sessions = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, sessions.ValueKind);
        Assert.Single(sessions.EnumerateArray());

        var session = sessions[0];
        Assert.Equal("skill-validator", session.GetProperty("db_type").GetString());
        Assert.Equal("nuget-trusted-publishing", session.GetProperty("skill_name").GetString());
        Assert.Equal("dry-run publish", session.GetProperty("scenario_name").GetString());
        Assert.Equal("baseline", session.GetProperty("role").GetString());
        Assert.Equal("completed", session.GetProperty("status").GetString());
        Assert.False(session.GetProperty("has_transcript").GetBoolean());
        Assert.Equal(0.75, session.GetProperty("metrics").GetProperty("score").GetDouble());
        Assert.Equal(true, session.GetProperty("judge").GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void DbFlag_EmptySkillValidatorDbWithJson_EmitsEmptyArray()
    {
        CreateSkillValidatorDb(withSession: false);

        var (stdout, stderr) = RunReplayWithArgs($"--db \"{_dbPath}\" --json");

        using var doc = JsonDocument.Parse(stdout);
        var sessions = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, sessions.ValueKind);
        Assert.Empty(sessions.EnumerateArray());
    }

    private void CreateSkillValidatorDb(bool withSession)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO schema_info (key, value) VALUES ('type', 'skill-validator');
            INSERT INTO schema_info (key, value) VALUES ('version', '1');

            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                skill_name TEXT NOT NULL,
                skill_path TEXT NOT NULL,
                scenario_name TEXT NOT NULL,
                run_index INTEGER NOT NULL,
                role TEXT NOT NULL,
                model TEXT NOT NULL,
                config_dir TEXT,
                work_dir TEXT,
                prompt TEXT,
                skill_sha TEXT,
                status TEXT NOT NULL DEFAULT 'running',
                started_at TEXT NOT NULL,
                completed_at TEXT
            );

            CREATE TABLE run_results (
                session_id TEXT PRIMARY KEY,
                metrics_json TEXT NOT NULL,
                judge_json TEXT,
                pairwise_json TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        if (!withSession)
            return;

        using var insertSession = conn.CreateCommand();
        insertSession.CommandText = """
            INSERT INTO sessions (
                id, skill_name, skill_path, scenario_name, run_index, role, model,
                config_dir, work_dir, prompt, skill_sha, status, started_at, completed_at
            ) VALUES (
                $id, $skillName, $skillPath, $scenarioName, $runIndex, $role, $model,
                $configDir, $workDir, $prompt, $skillSha, $status, $startedAt, $completedAt
            )
            """;
        insertSession.Parameters.AddWithValue("$id", "sess-1");
        insertSession.Parameters.AddWithValue("$skillName", "nuget-trusted-publishing");
        insertSession.Parameters.AddWithValue("$skillPath", "/tmp/skills/nuget-trusted-publishing");
        insertSession.Parameters.AddWithValue("$scenarioName", "dry-run publish");
        insertSession.Parameters.AddWithValue("$runIndex", 1);
        insertSession.Parameters.AddWithValue("$role", "baseline");
        insertSession.Parameters.AddWithValue("$model", "claude-opus-4.6");
        insertSession.Parameters.AddWithValue("$configDir", "missing-config");
        insertSession.Parameters.AddWithValue("$workDir", "/tmp/workdir");
        insertSession.Parameters.AddWithValue("$prompt", "Test prompt");
        insertSession.Parameters.AddWithValue("$skillSha", "abc123");
        insertSession.Parameters.AddWithValue("$status", "completed");
        insertSession.Parameters.AddWithValue("$startedAt", "2026-03-14T20:45:00Z");
        insertSession.Parameters.AddWithValue("$completedAt", "2026-03-14T20:45:30Z");
        insertSession.ExecuteNonQuery();

        using var insertRun = conn.CreateCommand();
        insertRun.CommandText = """
            INSERT INTO run_results (session_id, metrics_json, judge_json, pairwise_json)
            VALUES ($sessionId, $metricsJson, $judgeJson, $pairwiseJson)
            """;
        insertRun.Parameters.AddWithValue("$sessionId", "sess-1");
        insertRun.Parameters.AddWithValue("$metricsJson", "{\"score\":0.75,\"tokens\":123}");
        insertRun.Parameters.AddWithValue("$judgeJson", "{\"passed\":true}");
        insertRun.Parameters.AddWithValue("$pairwiseJson", "{\"winner\":\"skill\"}");
        insertRun.ExecuteNonQuery();
    }

    private static readonly string ReplayCs = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dotnet-replay.csproj"));

    private (string stdout, string stderr) RunReplayWithArgs(string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run -v q --project \"{ReplayCs}\" -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ReplayCs)!
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Process exited with code {process.ExitCode}. stderr: {stderr}");

        return (stdout, stderr);
    }
}
