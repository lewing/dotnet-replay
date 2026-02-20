using System.Text.Json;
using Xunit;

namespace ReplayTests;

public class JsonOutputTests
{
    private const string TestEventsDir = "TestData";

    [Fact]
    public void JsonOutput_ProducesValidJsonl()
    {
        var testFile = CreateMinimalEventsFile();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        
        foreach (var line in lines)
        {
            var exception = Record.Exception(() => JsonDocument.Parse(line));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void JsonOutput_ContainsRequiredFields()
    {
        var testFile = CreateEventsWithUserAndAssistant();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        
        var firstLine = JsonDocument.Parse(lines[0]);
        var root = firstLine.RootElement;
        
        Assert.True(root.TryGetProperty("turn", out _));
        Assert.True(root.TryGetProperty("role", out _));
        Assert.True(root.TryGetProperty("content_length", out _));
    }

    [Fact]
    public void JsonOutput_FilterUser_OnlyEmitsUserTurns()
    {
        var testFile = CreateEventsWithUserAndAssistant();
        var output = RunReplayWithArgs($"{testFile} --json --filter user --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            var role = doc.RootElement.GetProperty("role").GetString();
            Assert.Equal("user", role);
        }
    }

    [Fact]
    public void JsonOutput_FilterAssistant_OnlyEmitsAssistantTurns()
    {
        var testFile = CreateEventsWithUserAndAssistant();
        var output = RunReplayWithArgs($"{testFile} --json --filter assistant --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var doc = JsonDocument.Parse(line);
            var role = doc.RootElement.GetProperty("role").GetString();
            Assert.Equal("assistant", role);
        }
    }

    [Fact]
    public void JsonOutput_TailN_LimitsOutput()
    {
        var testFile = CreateEventsWithMultipleTurns(10);
        var output = RunReplayWithArgs($"{testFile} --json --tail 3 --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 3);
    }

    [Fact]
    public void JsonOutput_ExpandTools_IncludesToolData()
    {
        var testFile = CreateEventsWithToolCalls();
        var output = RunReplayWithArgs($"{testFile} --json --expand-tools --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        var toolLines = lines.Where(line => {
            var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("role", out var r) && r.GetString() == "tool";
        }).ToArray();
        
        Assert.NotEmpty(toolLines);
        var firstToolDoc = JsonDocument.Parse(toolLines[0]);
        Assert.True(firstToolDoc.RootElement.TryGetProperty("tool_name", out _));
    }

    [Fact]
    public void JsonOutput_AssistantWithToolCalls_IncludesToolCallsArray()
    {
        var testFile = CreateEventsWithToolCalls();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        var assistantLines = lines.Where(line => {
            var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("role", out var r) && r.GetString() == "assistant";
        }).ToArray();
        
        Assert.NotEmpty(assistantLines);
        var doc = JsonDocument.Parse(assistantLines[0]);
        
        if (doc.RootElement.TryGetProperty("tool_calls", out var toolCalls))
        {
            Assert.Equal(JsonValueKind.Array, toolCalls.ValueKind);
        }
    }

    [Fact]
    public void JsonOutput_TimestampFormat_IsIso8601()
    {
        var testFile = CreateMinimalEventsFile();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var doc = JsonDocument.Parse(lines[0]);
        
        if (doc.RootElement.TryGetProperty("timestamp", out var ts))
        {
            var tsString = ts.GetString();
            Assert.NotNull(tsString);
            Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", tsString);
        }
    }

    [Fact]
    public void JsonOutput_EmptySession_ProducesNoLines()
    {
        var testFile = CreateEmptyEventsFile();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(lines);
    }

    [Fact]
    public void JsonOutput_CombinedWithFilter_ProducesValidOutput()
    {
        var testFile = CreateEventsWithUserAndAssistant();
        var output = RunReplayWithArgs($"{testFile} --json --filter user --tail 1 --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 1);
        
        if (lines.Length > 0)
        {
            var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("user", doc.RootElement.GetProperty("role").GetString());
        }
    }

    // Helper methods for test data creation
    private string CreateMinimalEventsFile()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"minimal-{Guid.NewGuid()}.jsonl");
        
        var sessionStart = new {
            type = "session.start",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            data = new { sessionId = Guid.NewGuid().ToString() }
        };
        
        var userMsg = new {
            type = "user.message",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            data = new { content = "Hello" }
        };
        
        File.WriteAllLines(path, new[] {
            JsonSerializer.Serialize(sessionStart),
            JsonSerializer.Serialize(userMsg)
        });
        
        return path;
    }

    private string CreateEventsWithUserAndAssistant()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"user-assistant-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "User message" }
            },
            new {
                type = "assistant.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Assistant response" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateEventsWithMultipleTurns(int count)
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"multiple-{count}-{Guid.NewGuid()}.jsonl");
        
        var events = new List<object> {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            }
        };
        
        for (int i = 0; i < count; i++)
        {
            events.Add(new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.AddSeconds(i).ToString("o"),
                data = new { content = $"Message {i}" }
            });
        }
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateEventsWithToolCalls()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"tools-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Run tests" }
            },
            new {
                type = "assistant.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    content = "I'll run the tests",
                    toolRequests = new[] {
                        new { name = "powershell", args = new { command = "dotnet test" } }
                    }
                }
            },
            new {
                type = "tool.execution_start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    name = "powershell",
                    args = new { command = "dotnet test" }
                }
            },
            new {
                type = "tool.result",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    name = "powershell",
                    result = new {
                        status = "complete",
                        output = "Tests passed"
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateEmptyEventsFile()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"empty-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(path, "");
        return path;
    }

    private string RunReplayWithArgs(string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project .. -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");
        
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        
        return output;
    }
}
