using System.Text.Json;
using Xunit;

namespace ReplayTests;

public class SummaryOutputTests
{
    private const string TestEventsDir = "TestData";

    [Fact]
    public void Summary_PlainText_ContainsExpectedFields()
    {
        var testFile = CreateSessionWithActivity();
        var output = RunReplayWithArgs($"{testFile} --summary --stream");
        
        Assert.Contains("Session:", output);
        Assert.Contains("Duration:", output);
        Assert.Contains("Turns:", output);
        Assert.Contains("user", output);
        Assert.Contains("assistant", output);
        Assert.Contains("tool calls", output);
    }

    [Fact]
    public void Summary_Json_ProducesValidJson()
    {
        var testFile = CreateSessionWithActivity();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var exception = Record.Exception(() => JsonDocument.Parse(output));
        Assert.Null(exception);
    }

    [Fact]
    public void Summary_Json_ContainsRequiredFields()
    {
        var testFile = CreateSessionWithActivity();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        
        Assert.True(root.TryGetProperty("session_id", out _));
        Assert.True(root.TryGetProperty("duration_seconds", out _));
        Assert.True(root.TryGetProperty("turns", out var turns));
        Assert.Equal(JsonValueKind.Object, turns.ValueKind);
        Assert.True(turns.TryGetProperty("user", out _));
        Assert.True(turns.TryGetProperty("assistant", out _));
        Assert.True(turns.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public void Summary_CountsToolCalls()
    {
        var testFile = CreateSessionWithToolCalls();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var toolCalls = doc.RootElement
            .GetProperty("turns")
            .GetProperty("tool_calls")
            .GetInt32();
        
        Assert.True(toolCalls > 0);
    }

    [Fact]
    public void Summary_DetectsSkillInvocations()
    {
        var testFile = CreateSessionWithSkills();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var skills = doc.RootElement.GetProperty("skills_invoked");
        
        Assert.Equal(JsonValueKind.Array, skills.ValueKind);
        Assert.True(skills.GetArrayLength() > 0);
    }

    [Fact]
    public void Summary_ListsToolUsage()
    {
        var testFile = CreateSessionWithVariousTools();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var toolsUsed = doc.RootElement.GetProperty("tools_used");
        
        Assert.Equal(JsonValueKind.Object, toolsUsed.ValueKind);
        Assert.True(toolsUsed.EnumerateObject().Any());
    }

    [Fact]
    public void Summary_CountsErrors()
    {
        var testFile = CreateSessionWithErrors();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var errors = doc.RootElement.GetProperty("errors").GetInt32();
        
        Assert.True(errors > 0);
    }

    [Fact]
    public void Summary_PlainText_ShowsToolUsageTopN()
    {
        var testFile = CreateSessionWithManyTools();
        var output = RunReplayWithArgs($"{testFile} --summary --stream");
        
        Assert.Contains("Tools used:", output);
        Assert.Matches(@"\w+\s\(\d+\)", output); // Tool name followed by (count)
    }

    [Fact]
    public void Summary_EmptySession_HandlesGracefully()
    {
        var testFile = CreateEmptySession();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var turns = doc.RootElement.GetProperty("turns");
        
        Assert.Equal(0, turns.GetProperty("user").GetInt32());
        Assert.Equal(0, turns.GetProperty("assistant").GetInt32());
        Assert.Equal(0, turns.GetProperty("tool_calls").GetInt32());
    }

    [Fact]
    public void Summary_DurationFormatted_IsReadable()
    {
        var testFile = CreateSessionWithDuration();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var durationFormatted = doc.RootElement.GetProperty("duration_formatted").GetString();
        
        Assert.NotNull(durationFormatted);
        Assert.Matches(@"\d+[smh]", durationFormatted); // Contains seconds, minutes, or hours
    }

    [Fact]
    public void Summary_WithAgent_IncludesAgentName()
    {
        var testFile = CreateSessionWithAgent();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        if (doc.RootElement.TryGetProperty("agent", out var agent))
        {
            var agentName = agent.GetString();
            Assert.NotNull(agentName);
            Assert.NotEmpty(agentName);
        }
    }

    // Helper methods for test data creation
    private string CreateSessionWithActivity()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"activity-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = now.AddSeconds(1).ToString("o"),
                data = new { content = "User message" }
            },
            new {
                type = "assistant.message",
                timestamp = now.AddSeconds(2).ToString("o"),
                data = new {
                    content = "Response",
                    toolRequests = new[] {
                        new { name = "view", args = new { path = "file.txt" } }
                    }
                }
            },
            new {
                type = "session.end",
                timestamp = now.AddSeconds(10).ToString("o"),
                data = new { }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithToolCalls()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"toolcalls-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = now.AddSeconds(1).ToString("o"),
                data = new { content = "Show files" }
            },
            new {
                type = "assistant.message",
                timestamp = now.AddSeconds(2).ToString("o"),
                data = new {
                    content = "Listing files",
                    toolRequests = new object[] {
                        new { name = "view", args = new { path = "." } },
                        new { name = "glob", args = new { pattern = "**/*.cs" } }
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithSkills()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"skills-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = now.AddSeconds(1).ToString("o"),
                data = new { content = "Analyze CI" }
            },
            new {
                type = "assistant.message",
                timestamp = now.AddSeconds(2).ToString("o"),
                data = new {
                    content = "Using skill",
                    toolRequests = new[] {
                        new {
                            name = "skill",
                            args = new { skill = "ci-analysis" }
                        }
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithVariousTools()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"various-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "assistant.message",
                timestamp = now.AddSeconds(1).ToString("o"),
                data = new {
                    content = "Using tools",
                    toolRequests = new[] {
                        new { name = "view", args = new { } },
                        new { name = "edit", args = new { } },
                        new { name = "grep", args = new { } },
                        new { name = "view", args = new { } } // Duplicate for counting
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithErrors()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"errors-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "tool.result",
                timestamp = now.AddSeconds(1).ToString("o"),
                data = new {
                    name = "view",
                    result = new {
                        status = "error",
                        output = "File not found"
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithManyTools()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"many-{Guid.NewGuid()}.jsonl");
        
        var now = DateTimeOffset.UtcNow;
        var tools = new[] { "view", "edit", "grep", "glob", "powershell", "task", "sql", "create", "web_search", "git", "skill" };
        
        var events = new List<object> {
            new {
                type = "session.start",
                timestamp = now.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            }
        };
        
        events.Add(new {
            type = "assistant.message",
            timestamp = now.AddSeconds(1).ToString("o"),
            data = new {
                content = "Using many tools",
                toolRequests = tools.Select(t => new { name = t, args = new { } }).ToArray()
            }
        });
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateEmptySession()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"empty-session-{Guid.NewGuid()}.jsonl");
        
        var sessionStart = new {
            type = "session.start",
            timestamp = DateTimeOffset.UtcNow.ToString("o"),
            data = new { sessionId = Guid.NewGuid().ToString() }
        };
        
        File.WriteAllLines(path, new[] { JsonSerializer.Serialize(sessionStart) });
        return path;
    }

    private string CreateSessionWithDuration()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"duration-{Guid.NewGuid()}.jsonl");
        
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var end = DateTimeOffset.UtcNow;
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = start.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = start.AddMinutes(1).ToString("o"),
                data = new { content = "Test" }
            },
            new {
                type = "session.end",
                timestamp = end.ToString("o"),
                data = new { }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithAgent()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"agent-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    sessionId = Guid.NewGuid().ToString(),
                    context = new { agentName = "TestAgent" }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
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
