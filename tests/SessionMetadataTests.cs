using System.Reflection;
using Xunit;

namespace ReplayTests;

public class SessionMetadataTests : IDisposable
{
    private readonly string _tempDir;

    public SessionMetadataTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"replay-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void EnrichCopilotSessionMetadata_ReadsBranchAndRepositoryFromWorkspaceYaml()
    {
        var yamlPath = Path.Combine(_tempDir, "workspace.yaml");
        var eventsPath = Path.Combine(_tempDir, "events.jsonl");
        File.WriteAllText(yamlPath, "branch: feature/ui\nrepository: lewing/dotnet-replay\n");
        File.WriteAllText(eventsPath, "{}\n");

        var result = InvokeEnrichCopilotSessionMetadata(yamlPath, eventsPath, "", "");

        Assert.Equal("feature/ui", result.Branch);
        Assert.Equal("lewing/dotnet-replay", result.Repository);
    }

    [Fact]
    public void EnrichCopilotSessionMetadata_FallsBackToSessionStartContext()
    {
        var yamlPath = Path.Combine(_tempDir, "workspace.yaml");
        var eventsPath = Path.Combine(_tempDir, "events.jsonl");
        File.WriteAllText(yamlPath, "summary: Missing metadata\n");
        File.WriteAllText(eventsPath,
            "{" +
            "\"type\":\"session.start\"," +
            "\"data\":{\"context\":{\"branch\":\"feature/events\",\"repository\":\"dotnet/runtime\"}}}" +
            "\n");

        var result = InvokeEnrichCopilotSessionMetadata(yamlPath, eventsPath, "", "");

        Assert.Equal("feature/events", result.Branch);
        Assert.Equal("dotnet/runtime", result.Repository);
    }

    [Fact]
    public void EnrichCopilotSessionMetadata_PreservesExistingValues()
    {
        var yamlPath = Path.Combine(_tempDir, "workspace.yaml");
        var eventsPath = Path.Combine(_tempDir, "events.jsonl");
        File.WriteAllText(yamlPath, "branch: yaml-branch\nrepository: yaml/repo\n");
        File.WriteAllText(eventsPath,
            "{" +
            "\"type\":\"session.start\"," +
            "\"data\":{\"context\":{\"branch\":\"event-branch\",\"repository\":\"event/repo\"}}}" +
            "\n");

        var result = InvokeEnrichCopilotSessionMetadata(yamlPath, eventsPath, "db-branch", "db/repo");

        Assert.Equal("db-branch", result.Branch);
        Assert.Equal("db/repo", result.Repository);
    }

    [Fact]
    public void ReadClaudeBranch_ReadsGitBranch()
    {
        var eventsPath = Path.Combine(_tempDir, "claude.jsonl");
        File.WriteAllText(eventsPath,
            "{" +
            "\"type\":\"user\"," +
            "\"gitBranch\":\"feature/claude\"}" +
            "\n");

        var branch = InvokeReadClaudeBranch(eventsPath);

        Assert.Equal("feature/claude", branch);
    }

    private static (string Branch, string Repository) InvokeEnrichCopilotSessionMetadata(string yamlPath, string eventsPath, string branch, string repository)
    {
        var type = LoadSessionBrowserType();
        var method = type.GetMethod("EnrichCopilotSessionMetadata", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnrichCopilotSessionMetadata not found.");
        var result = method.Invoke(null, [yamlPath, eventsPath, branch, repository])
            ?? throw new InvalidOperationException("EnrichCopilotSessionMetadata returned null.");
        var resultType = result.GetType();
        return (
            (string)(resultType.GetField("Item1")?.GetValue(result) ?? resultType.GetProperty("Branch")?.GetValue(result) ?? ""),
            (string)(resultType.GetField("Item2")?.GetValue(result) ?? resultType.GetProperty("Repository")?.GetValue(result) ?? "")
        );
    }

    private static string InvokeReadClaudeBranch(string eventsPath)
    {
        var type = LoadSessionBrowserType();
        var method = type.GetMethod("ReadClaudeBranch", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReadClaudeBranch not found.");
        return (string)(method.Invoke(null, [eventsPath]) ?? "");
    }

    private static Type LoadSessionBrowserType()
    {
        var asm = Assembly.Load("dotnet-replay");
        return asm.GetType("SessionBrowser", throwOnError: true)!;
    }
}
