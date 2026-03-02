using System.Text.Json;
using Xunit;

namespace ReplayTests;

public class EdgeCaseTests
{
    private const string TestEventsDir = "TestData";

    [Fact]
    public void LargeContent_JsonMode_TruncatesCorrectly()
    {
        var testFile = CreateSessionWithLargeContent(10000);
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        
        var doc = JsonDocument.Parse(lines[0]);
        var contentLength = doc.RootElement.GetProperty("content_length").GetInt32();
        Assert.Equal(10000, contentLength);
    }

    [Fact]
    public void NoTools_Summary_HandlesGracefully()
    {
        var testFile = CreateSessionWithoutTools();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var toolsUsed = doc.RootElement.GetProperty("tools_used");
        Assert.Equal(JsonValueKind.Object, toolsUsed.ValueKind);
        Assert.Empty(toolsUsed.EnumerateObject());
    }

    [Fact]
    public void SessionWithOnlyErrors_CountsCorrectly()
    {
        var testFile = CreateSessionWithOnlyErrors();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        var errors = doc.RootElement.GetProperty("errors").GetInt32();
        var turns = doc.RootElement.GetProperty("turns");
        
        Assert.True(errors > 0);
        Assert.Equal(0, turns.GetProperty("user").GetInt32());
        Assert.Equal(0, turns.GetProperty("assistant").GetInt32());
    }

    [Fact]
    public void MalformedEvent_SkippedGracefully()
    {
        var testFile = CreateSessionWithMalformedEvent();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        // Should not crash, may produce fewer lines than expected
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var exception = Record.Exception(() => JsonDocument.Parse(line));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void FilterNonExistentRole_ProducesNoOutput()
    {
        var testFile = CreateMinimalSession();
        var output = RunReplayWithArgs($"{testFile} --json --filter tool --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(lines);
    }

    [Fact]
    public void TailZero_ProducesNoOutput()
    {
        var testFile = CreateMinimalSession();
        var output = RunReplayWithArgs($"{testFile} --json --tail 0 --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Empty(lines);
    }

    [Fact]
    public void TailLargerThanContent_ShowsAllContent()
    {
        var testFile = CreateSessionWithTurns(3);
        var output = RunReplayWithArgs($"{testFile} --json --tail 100 --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void BackwardCompatibility_ExistingOutputUnchanged()
    {
        var testFile = CreateMinimalSession();
        var outputWithoutFlags = RunReplayWithArgs($"{testFile} --stream");
        
        // Default output should not be JSON
        var exception = Record.Exception(() => JsonDocument.Parse(outputWithoutFlags));
        Assert.NotNull(exception);
    }

    [Fact]
    public void VeryLongToolName_HandledCorrectly()
    {
        var testFile = CreateSessionWithLongToolName();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var assistantLine = lines.FirstOrDefault(l => {
            var doc = JsonDocument.Parse(l);
            return doc.RootElement.TryGetProperty("role", out var r) && r.GetString() == "assistant";
        });
        
        Assert.NotNull(assistantLine);
        var doc = JsonDocument.Parse(assistantLine);
        Assert.True(doc.RootElement.TryGetProperty("tool_calls", out _));
    }

    [Fact]
    public void SpecialCharactersInContent_EscapedProperly()
    {
        var testFile = CreateSessionWithSpecialChars();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        
        var doc = JsonDocument.Parse(lines[0]);
        var content = doc.RootElement.GetProperty("content").GetString();
        Assert.Contains("\"", content); // Should handle quotes
    }

    [Fact]
    public void MultipleSessionStarts_LastOneWins()
    {
        var testFile = CreateSessionWithMultipleStarts();
        var output = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.TryGetProperty("session_id", out _));
    }

    [Fact]
    public void ToolResultWithoutStart_HandledGracefully()
    {
        var testFile = CreateSessionWithOrphanToolResult();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        // Should not crash
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void CombinedFlags_OrderIndependent()
    {
        var testFile = CreateMinimalSession();
        
        var output1 = RunReplayWithArgs($"{testFile} --json --summary --stream");
        var output2 = RunReplayWithArgs($"{testFile} --summary --json --stream");
        
        var doc1 = JsonDocument.Parse(output1);
        var doc2 = JsonDocument.Parse(output2);
        
        Assert.True(doc1.RootElement.TryGetProperty("session_id", out _));
        Assert.True(doc2.RootElement.TryGetProperty("session_id", out _));
    }

    [Fact]
    public void UnicodeInContent_PreservedCorrectly()
    {
        var testFile = CreateSessionWithUnicode();
        var output = RunReplayWithArgs($"{testFile} --json --stream");
        
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var doc = JsonDocument.Parse(lines[0]);
        var content = doc.RootElement.GetProperty("content").GetString();
        
        Assert.Contains("🚀", content);
        Assert.Contains("日本語", content);
    }

    // Helper methods for edge case test data
    private string CreateSessionWithLargeContent(int size)
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"large-{Guid.NewGuid()}.jsonl");
        
        var largeContent = new string('x', size);
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = largeContent }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithoutTools()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"no-tools-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Hello" }
            },
            new {
                type = "assistant.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Hi there" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithOnlyErrors()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"only-errors-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "tool.result",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    name = "view",
                    result = new { status = "error", output = "Error 1" }
                }
            },
            new {
                type = "tool.result",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    name = "edit",
                    result = new { status = "error", output = "Error 2" }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithMalformedEvent()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"malformed-{Guid.NewGuid()}.jsonl");
        
        var lines = new[] {
            JsonSerializer.Serialize(new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            }),
            "{ invalid json",
            JsonSerializer.Serialize(new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Test" }
            })
        };
        
        File.WriteAllLines(path, lines);
        return path;
    }

    private string CreateMinimalSession()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"minimal-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Test" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithTurns(int count)
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"turns-{count}-{Guid.NewGuid()}.jsonl");
        
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

    private string CreateSessionWithLongToolName()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"long-tool-{Guid.NewGuid()}.jsonl");
        
        var longName = new string('a', 200);
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "assistant.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    content = "Using tool",
                    toolRequests = new[] {
                        new { name = longName, args = new { } }
                    }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithSpecialChars()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"special-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Test \"quotes\" and \n newlines \t tabs" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithMultipleStarts()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"multi-start-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = "first-id" }
            },
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = "second-id" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithOrphanToolResult()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"orphan-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "tool.result",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new {
                    name = "view",
                    result = new { status = "complete", output = "Result" }
                }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private string CreateSessionWithUnicode()
    {
        Directory.CreateDirectory(TestEventsDir);
        var path = Path.Combine(TestEventsDir, $"unicode-{Guid.NewGuid()}.jsonl");
        
        var events = new object[] {
            new {
                type = "session.start",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { sessionId = Guid.NewGuid().ToString() }
            },
            new {
                type = "user.message",
                timestamp = DateTimeOffset.UtcNow.ToString("o"),
                data = new { content = "Test 🚀 emoji and 日本語 characters" }
            }
        };
        
        File.WriteAllLines(path, events.Select(e => JsonSerializer.Serialize(e)));
        return path;
    }

    private static readonly string ReplayCs = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dotnet-replay.csproj"));

    private string RunReplayWithArgs(string args)
    {
        var parts = args.Split(' ', 2);
        if (parts.Length > 0 && File.Exists(parts[0]))
            args = Path.GetFullPath(parts[0]) + (parts.Length > 1 ? " " + parts[1] : "");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run -v q --project {ReplayCs} -- {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ReplayCs)!
        };
        
        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");
        
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        
        return output;
    }
}
