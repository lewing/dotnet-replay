using System.Text.Json;
using Xunit;

namespace ReplayTests;

public class ReplayHelpersTests
{
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("", 0)]
    [InlineData("abc 123", 7)]
    public void VisibleWidth_AsciiStrings(string input, int expected)
    {
        Assert.Equal(expected, ReplayHelpers.VisibleWidth(input));
    }

    [Fact]
    public void VisibleWidth_WideChars_CountAsTwo()
    {
        // CJK character U+4E16 (世) should count as 2
        Assert.Equal(2, ReplayHelpers.VisibleWidth("世"));
        Assert.Equal(4, ReplayHelpers.VisibleWidth("世界"));
        // Mixed: 5 ASCII + 2 wide = 9
        Assert.Equal(9, ReplayHelpers.VisibleWidth("Hello世界"));
    }

    [Theory]
    [InlineData(0, "+0.0s")]
    [InlineData(0.05, "+0.0s")]
    [InlineData(5, "+5.0s")]
    [InlineData(30, "+30.0s")]
    [InlineData(59.9, "+59.9s")]
    public void FormatRelativeTime_Seconds(double totalSeconds, string expected)
    {
        Assert.Equal(expected, ReplayHelpers.FormatRelativeTime(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void FormatRelativeTime_Minutes()
    {
        Assert.Equal("+1m 30s", ReplayHelpers.FormatRelativeTime(TimeSpan.FromSeconds(90)));
        Assert.Equal("+5m 0s", ReplayHelpers.FormatRelativeTime(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void FormatRelativeTime_Hours()
    {
        Assert.Equal("+1h 30m", ReplayHelpers.FormatRelativeTime(TimeSpan.FromMinutes(90)));
        Assert.Equal("+2h 0m", ReplayHelpers.FormatRelativeTime(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void IsClaudeFormat_WithClaudeJsonl_ReturnsTrue()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """
                {"type":"user","message":{"role":"user","content":"hello"}}
                {"type":"assistant","message":{"role":"assistant","content":"hi"}}
                """);
            Assert.True(ReplayHelpers.IsClaudeFormat(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void IsClaudeFormat_WithNonClaudeJsonl_ReturnsFalse()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile,
                """
                {"event":"user.message","data":{"content":"hello"}}
                {"event":"assistant.message","data":{"content":"hi"}}
                """);
            Assert.False(ReplayHelpers.IsClaudeFormat(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void IsClaudeFormat_EmptyFile_ReturnsFalse()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            Assert.False(ReplayHelpers.IsClaudeFormat(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void SafeGetString_ExistingProperty_ReturnsValue()
    {
        using var doc = JsonDocument.Parse("""{"name":"test","count":42}""");
        Assert.Equal("test", ReplayHelpers.SafeGetString(doc.RootElement, "name"));
    }

    [Fact]
    public void SafeGetString_MissingProperty_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"name":"test"}""");
        Assert.Equal("", ReplayHelpers.SafeGetString(doc.RootElement, "missing"));
    }

    [Fact]
    public void SafeGetString_NonStringProperty_ReturnsEmpty()
    {
        using var doc = JsonDocument.Parse("""{"count":42}""");
        Assert.Equal("", ReplayHelpers.SafeGetString(doc.RootElement, "count"));
    }

    [Theory]
    [InlineData(30, "now")]
    [InlineData(300, "5m")]
    [InlineData(7200, "2h")]
    [InlineData(172800, "2d")]
    [InlineData(2592000, "1mo")]
    public void FormatAge_VariousTimespans(double totalSeconds, string expected)
    {
        Assert.Equal(expected, ReplayHelpers.FormatAge(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Theory]
    [InlineData(500, "500B")]
    [InlineData(2048, "2KB")]
    [InlineData(1572864, "1.5MB")]
    public void FormatFileSize_VariousSizes(long bytes, string expected)
    {
        Assert.Equal(expected, ReplayHelpers.FormatFileSize(bytes));
    }
}
