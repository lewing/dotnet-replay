using System.Text.Json;
using Xunit;

namespace ReplayTests;

public class DbPathTests
{
    [Fact]
    public void DbFlag_NonExistentFile_ShowsError()
    {
        var nonExistentDb = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid()}.db");
        var (stdout, stderr) = RunReplayWithArgs($"--db {nonExistentDb}");
        
        // Should show an error on stderr (app can't browse when output is redirected)
        Assert.Contains("Error: Cannot use --db in redirected output", stderr);
    }

    [Fact]
    public void DbFlag_ValidDbButPipedOutput_ShowsRedirectionError()
    {
        var tempDb = CreateEmptyDbFile();
        var (stdout, stderr) = RunReplayWithArgs($"--db {tempDb}");
        
        Assert.Contains("Error: Cannot use --db in redirected output", stderr);
        
        // Clean up
        try { File.Delete(tempDb); } catch { }
    }

    [Fact]
    public void PositionalDbFile_AutoDetection_BehavesLikeDbFlag()
    {
        var tempDb = CreateEmptyDbFile();
        var (stdout, stderr) = RunReplayWithArgs(tempDb);
        
        // Should behave like --db: show redirected output error
        Assert.Contains("Error: Cannot use --db in redirected output", stderr);
        
        // Clean up
        try { File.Delete(tempDb); } catch { }
    }

    [Fact]
    public void DbFlag_MissingArgument_ShowsError()
    {
        var (stdout, stderr) = RunReplayWithArgs("--db");
        
        Assert.Contains("Error: --db requires a file path", stderr);
    }

    [Fact]
    public void HelpText_IncludesDbFlag()
    {
        var (stdout, stderr) = RunReplayWithArgs("--help");
        
        Assert.Contains("--db", stdout);
        Assert.Contains("Browse sessions from an external session-store.db file", stdout);
    }

    // Helper to create an empty DB file
    private string CreateEmptyDbFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}.db");
        // Create an empty file (doesn't need to be a valid SQLite DB for these tests)
        File.WriteAllText(path, "");
        return path;
    }

    private static readonly string ReplayCs = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "replay.cs"));

    private (string stdout, string stderr) RunReplayWithArgs(string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run {ReplayCs} -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // Need to redirect input too for proper TTY detection
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ReplayCs)!
        };
        
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");
        
        process.StandardInput.Close(); // Close stdin immediately
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        
        return (stdout, stderr);
    }
}
