using Microsoft.Data.Sqlite;
using Xunit;

namespace ReplayTests;

/// <summary>
/// Tests for checkpoint navigation feature (Tier 1).
/// Exercises DataParsers.LoadCheckpointsForSession against various DB states.
/// </summary>
public class CheckpointTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public CheckpointTests()
    {
        // In-memory DB — fast and auto-disposed
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose()
    {
        _conn.Dispose();
    }

    // ---- Schema helpers ----

    private void CreateCheckpointsTable()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE checkpoints (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                checkpoint_number INTEGER NOT NULL,
                title TEXT,
                overview TEXT,
                history TEXT,
                work_done TEXT,
                technical_details TEXT,
                important_files TEXT,
                next_steps TEXT,
                created_at TEXT DEFAULT (datetime('now'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private void InsertCheckpoint(string sessionId, int number, string? title = "Test",
        string? overview = null, string? workDone = null, string? nextSteps = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkpoints (session_id, checkpoint_number, title, overview, work_done, next_steps)
            VALUES ($sid, $num, $title, $overview, $workDone, $nextSteps)
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$num", number);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$overview", (object?)overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$workDone", (object?)workDone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nextSteps", (object?)nextSteps ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SeedStandardCheckpoints()
    {
        CreateCheckpointsTable();

        // Checkpoint 0: null title, null overview (edge case)
        InsertCheckpoint("sess-a", 0, title: null, overview: null, workDone: "", nextSteps: "Write tests");

        // Checkpoint 1: fully populated
        InsertCheckpoint("sess-a", 1, title: "Initial implementation", overview: "Built the parser",
            workDone: "Parser complete", nextSteps: "Add error recovery");

        // Checkpoint 2: null work_done, empty next_steps
        InsertCheckpoint("sess-a", 2, title: "Edge cases", overview: "Handling edge cases",
            workDone: null, nextSteps: "");
    }

    // ---- Tests ----

    [Fact]
    public void LoadCheckpoints_HappyPath_ReturnsCheckpointsForSession()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-a");

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void LoadCheckpoints_EmptyResult_SessionWithNoCheckpoints()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-b");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadCheckpoints_EmptyResult_NonexistentSession()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "does-not-exist");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadCheckpoints_MissingTable_ReturnsEmptyList()
    {
        // No checkpoints table created — should not throw
        var result = DataParsers.LoadCheckpointsForSession(_conn, "any-session");

        Assert.Empty(result);
    }

    [Fact]
    public void LoadCheckpoints_NullTitle_GetsFallbackName()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-a");

        var cp0 = result.First(r => r.CheckpointNumber == 0);
        // Batty's impl: null title → "Checkpoint {N}"
        Assert.Equal("Checkpoint 0", cp0.Title);
    }

    [Fact]
    public void LoadCheckpoints_NullFields_HandledGracefully()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-a");

        // Checkpoint 0: null overview
        var cp0 = result.First(r => r.CheckpointNumber == 0);
        Assert.Null(cp0.Overview);

        // Checkpoint 2: null work_done
        var cp2 = result.First(r => r.CheckpointNumber == 2);
        Assert.Null(cp2.WorkDone);
        Assert.Equal("", cp2.NextSteps);
    }

    [Fact]
    public void LoadCheckpoints_Ordering_ReturnedByCheckpointNumber()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-a");

        Assert.Equal(3, result.Count);
        Assert.Equal(0, result[0].CheckpointNumber);
        Assert.Equal(1, result[1].CheckpointNumber);
        Assert.Equal(2, result[2].CheckpointNumber);
    }

    [Fact]
    public void LoadCheckpoints_Ordering_ReverseInsertionStillOrdered()
    {
        CreateCheckpointsTable();
        InsertCheckpoint("sess-r", 3, title: "Third");
        InsertCheckpoint("sess-r", 1, title: "First");
        InsertCheckpoint("sess-r", 2, title: "Second");

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-r");

        Assert.Equal(3, result.Count);
        Assert.Equal("First", result[0].Title);
        Assert.Equal("Second", result[1].Title);
        Assert.Equal("Third", result[2].Title);
    }

    [Fact]
    public void LoadCheckpoints_FullyPopulatedRow_AllFieldsRead()
    {
        SeedStandardCheckpoints();

        var result = DataParsers.LoadCheckpointsForSession(_conn, "sess-a");
        var cp1 = result.First(r => r.CheckpointNumber == 1);

        Assert.Equal(1, cp1.CheckpointNumber);
        Assert.Equal("Initial implementation", cp1.Title);
        Assert.Equal("Built the parser", cp1.Overview);
        Assert.Equal("Parser complete", cp1.WorkDone);
        Assert.Equal("Add error recovery", cp1.NextSteps);
        Assert.NotNull(cp1.CreatedAt);
    }

    [Fact]
    public void LoadCheckpoints_IsolatesBySessionId_NoLeakage()
    {
        CreateCheckpointsTable();
        InsertCheckpoint("sess-x", 0, title: "X checkpoint");
        InsertCheckpoint("sess-y", 0, title: "Y checkpoint");
        InsertCheckpoint("sess-x", 1, title: "X second");

        var resultX = DataParsers.LoadCheckpointsForSession(_conn, "sess-x");
        var resultY = DataParsers.LoadCheckpointsForSession(_conn, "sess-y");

        Assert.Equal(2, resultX.Count);
        Assert.Single(resultY);
        Assert.All(resultX, r => Assert.StartsWith("X", r.Title));
        Assert.All(resultY, r => Assert.StartsWith("Y", r.Title));
    }

    [Fact]
    public void LoadCheckpoints_EmptyTable_ReturnsEmptyList()
    {
        CreateCheckpointsTable();
        // Table exists but has no rows

        var result = DataParsers.LoadCheckpointsForSession(_conn, "any-session");

        Assert.Empty(result);
    }
}

