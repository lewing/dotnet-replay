using Microsoft.Data.Sqlite;
using Xunit;

namespace ReplayTests;

/// <summary>
/// Tests for cross-session FTS5 search feature (Tier 2).
/// Exercises DataParsers.SearchSessions against various DB states.
/// </summary>
public class SearchTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public SearchTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose()
    {
        _conn.Dispose();
    }

    // ---- Schema helpers ----

    private void CreateSearchIndex()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE VIRTUAL TABLE search_index USING fts5(content, session_id, source_type, source_id)";
        cmd.ExecuteNonQuery();
    }

    private void InsertSearchEntry(string content, string sessionId, string sourceType, string? sourceId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO search_index (content, session_id, source_type, source_id)
            VALUES ($content, $sid, $sourceType, $sourceId)
            """;
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$sourceType", sourceType);
        cmd.Parameters.AddWithValue("$sourceId", (object?)sourceId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SeedStandardSearchData()
    {
        CreateSearchIndex();

        // Session A — coding session about OAuth
        InsertSearchEntry(
            "I need to implement OAuth redirect flow for the login page",
            "sess-a", "turn", "turn-1");
        InsertSearchEntry(
            "The OAuth token refresh logic should handle expired tokens gracefully",
            "sess-a", "turn", "turn-5");
        InsertSearchEntry(
            "Implemented OAuth redirect and token refresh",
            "sess-a", "checkpoint_overview", "cp-1");

        // Session B — debugging sockets
        InsertSearchEntry(
            "Getting a socket timeout error when connecting to the database",
            "sess-b", "turn", "turn-2");
        InsertSearchEntry(
            "Fixed the connection pool to avoid socket timeout under load",
            "sess-b", "checkpoint_overview", "cp-1");

        // Session C — also mentions OAuth but in a different context
        InsertSearchEntry(
            "Reviewed the OAuth scopes configuration for the API gateway",
            "sess-c", "turn", "turn-3");
    }

    // ---- Happy path ----

    [Fact]
    public void SearchSessions_HappyPath_ReturnsMatchingResults()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "OAuth");

        Assert.NotEmpty(results);
        // Snippet contains the matched term (possibly with »/« markers from FTS5 snippet())
        Assert.All(results, r => Assert.Contains("OAuth", r.Snippet, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchSessions_HappyPath_PopulatesAllFields()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "socket timeout");

        Assert.NotEmpty(results);
        var first = results[0];
        Assert.False(string.IsNullOrEmpty(first.Snippet));
        Assert.False(string.IsNullOrEmpty(first.SessionId));
        Assert.False(string.IsNullOrEmpty(first.SourceType));
        // FTS5 rank is a negative number (closer to 0 = better match)
        Assert.True(first.Rank < 0 || first.Rank != 0, "Rank should be a non-zero FTS5 score");
    }

    [Fact]
    public void SearchSessions_HappyPath_SnippetContainsHighlightMarkers()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "OAuth");

        Assert.NotEmpty(results);
        // Batty's implementation uses »/« as snippet markers
        var hasMarkers = results.Any(r => r.Snippet.Contains('»') || r.Snippet.Contains('«'));
        Assert.True(hasMarkers, "Expected at least one snippet to contain »/« highlight markers");
    }

    // ---- Empty / whitespace query ----

    [Fact]
    public void SearchSessions_EmptyQuery_ReturnsEmptyList()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "");

        Assert.Empty(results);
    }

    [Fact]
    public void SearchSessions_WhitespaceQuery_ReturnsEmptyList()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "   ");

        Assert.Empty(results);
    }

    [Fact]
    public void SearchSessions_NullQuery_ReturnsEmptyList()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, null!);

        Assert.Empty(results);
    }

    // ---- No matches ----

    [Fact]
    public void SearchSessions_NoMatches_ReturnsEmptyList()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "xyzzyplugh");

        Assert.Empty(results);
    }

    [Fact]
    public void SearchSessions_EmptyTable_ReturnsEmptyList()
    {
        CreateSearchIndex();
        // Table exists but has no rows

        var results = DataParsers.SearchSessions(_conn, "anything");

        Assert.Empty(results);
    }

    // ---- Missing table ----

    [Fact]
    public void SearchSessions_MissingTable_ReturnsEmptyList()
    {
        // No search_index table created — should not throw
        var results = DataParsers.SearchSessions(_conn, "test");

        Assert.Empty(results);
    }

    // ---- Ordering by relevance ----

    [Fact]
    public void SearchSessions_ResultsOrderedByRelevance()
    {
        CreateSearchIndex();

        // Insert entries with varying relevance for "timeout"
        InsertSearchEntry("timeout timeout timeout error", "sess-x", "turn", "t-1");
        InsertSearchEntry("a minor timeout happened once", "sess-y", "turn", "t-2");

        var results = DataParsers.SearchSessions(_conn, "timeout");

        Assert.Equal(2, results.Count);
        // FTS5 rank: more negative = better match. First result should be the better match.
        Assert.True(results[0].Rank <= results[1].Rank,
            $"Expected first result rank ({results[0].Rank}) <= second ({results[1].Rank}) (FTS5: lower rank = better)");
    }

    // ---- Limit ----

    [Fact]
    public void SearchSessions_RespectsLimit()
    {
        CreateSearchIndex();

        // Insert more entries than the limit
        for (int i = 0; i < 10; i++)
        {
            InsertSearchEntry($"Session entry about testing number {i}", $"sess-{i}", "turn", $"t-{i}");
        }

        var results = DataParsers.SearchSessions(_conn, "testing", limit: 3);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void SearchSessions_DefaultLimit_ReturnsUpTo50()
    {
        CreateSearchIndex();

        for (int i = 0; i < 60; i++)
        {
            InsertSearchEntry($"Entry about debugging item {i}", $"sess-{i}", "turn", $"t-{i}");
        }

        // Default limit is 50
        var results = DataParsers.SearchSessions(_conn, "debugging");

        Assert.True(results.Count <= 50, $"Expected at most 50 results, got {results.Count}");
        Assert.True(results.Count > 0, "Expected some results");
    }

    // ---- Multiple sessions ----

    [Fact]
    public void SearchSessions_SpansMultipleSessions()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "OAuth");

        var sessionIds = results.Select(r => r.SessionId).Distinct().ToList();
        Assert.True(sessionIds.Count >= 2,
            $"Expected results from at least 2 sessions, got {sessionIds.Count}: [{string.Join(", ", sessionIds)}]");
    }

    [Fact]
    public void SearchSessions_ReturnsCorrectSessionIds()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "OAuth");

        var sessionIds = results.Select(r => r.SessionId).Distinct().OrderBy(s => s).ToList();
        Assert.Contains("sess-a", sessionIds);
        Assert.Contains("sess-c", sessionIds);
        Assert.DoesNotContain("sess-b", sessionIds);
    }

    // ---- Source types ----

    [Fact]
    public void SearchSessions_ReturnsMultipleSourceTypes()
    {
        SeedStandardSearchData();

        var results = DataParsers.SearchSessions(_conn, "OAuth");

        var sourceTypes = results.Select(r => r.SourceType).Distinct().ToList();
        Assert.Contains("turn", sourceTypes);
        Assert.Contains("checkpoint_overview", sourceTypes);
    }

    // ---- Nullable SourceId ----

    [Fact]
    public void SearchSessions_NullSourceId_HandledGracefully()
    {
        CreateSearchIndex();
        InsertSearchEntry("content with null source id", "sess-n", "turn", null);

        var results = DataParsers.SearchSessions(_conn, "content");

        Assert.Single(results);
        Assert.Null(results[0].SourceId);
    }

    // ---- Special characters ----

    [Fact]
    public void SearchSessions_PrefixQuery_HandledGracefully()
    {
        SeedStandardSearchData();

        // FTS5 supports prefix queries with *
        var results = DataParsers.SearchSessions(_conn, "time*");

        // Should not throw — may match "timeout"
        Assert.NotNull(results);
    }

    [Fact]
    public void SearchSessions_QuotedPhrase_HandledGracefully()
    {
        SeedStandardSearchData();

        // FTS5 supports quoted phrases
        var results = DataParsers.SearchSessions(_conn, "\"socket timeout\"");

        Assert.NotNull(results);
        if (results.Count > 0)
        {
            Assert.All(results, r => Assert.Contains("socket", r.Snippet, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SearchSessions_BooleanOr_HandledGracefully()
    {
        SeedStandardSearchData();

        // FTS5 supports OR queries
        var results = DataParsers.SearchSessions(_conn, "OAuth OR socket");

        Assert.NotNull(results);
        if (results.Count > 0)
        {
            var sessionIds = results.Select(r => r.SessionId).Distinct().ToList();
            Assert.True(sessionIds.Count >= 2, "OR query should match across multiple sessions");
        }
    }

    [Fact]
    public void SearchSessions_MalformedQuery_ReturnsEmptyGracefully()
    {
        SeedStandardSearchData();

        // Unbalanced quotes — FTS5 would reject this
        var results = DataParsers.SearchSessions(_conn, "\"unclosed quote");

        // Should not throw; may return empty or partial results
        Assert.NotNull(results);
    }
}
