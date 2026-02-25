using System.Text.Json;
using Xunit;

namespace ReplayTests;

/// <summary>
/// Tests for the `replay stats` command that aggregates statistics across multiple transcript files.
/// Uses the Waza "tasks" format (EvaluationOutcome) which includes config.model_id, validations, etc.
/// </summary>
public class StatsOutputTests
{
    private static readonly string TestDataDir = Path.Combine(Path.GetTempPath(), "replay-stats-tests");

    public StatsOutputTests()
    {
        Directory.CreateDirectory(TestDataDir);
    }

    // ========== Basic Aggregation Tests ==========

    [Fact]
    public void Stats_MultipleFiles_AggregatesCorrectly()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", false, 150),
            ("model-b", true, 200)
        });

        var output = RunStats(string.Join(" ", files));

        Assert.Contains("3 sessions", output);
        Assert.Contains("Passed:", output);
        Assert.Contains("Failed:", output);
    }

    [Fact]
    public void Stats_Json_ProducesValidJson()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", false, 150)
        });

        var output = RunStats($"{string.Join(" ", files)} --json");

        var exception = Record.Exception(() => JsonDocument.Parse(output));
        Assert.Null(exception);
    }

    [Fact]
    public void Stats_Json_ContainsRequiredFields()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-b", false, 150)
        });

        var output = RunStats($"{string.Join(" ", files)} --json");
        var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("total_files", out _));
        Assert.True(root.TryGetProperty("passed", out _));
        Assert.True(root.TryGetProperty("failed", out _));
        Assert.True(root.TryGetProperty("pass_rate", out _));
    }

    [Fact]
    public void Stats_AverageDuration_CalculatesCorrectly()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 1000),
            ("model-a", true, 2000),
            ("model-a", true, 3000)
        });

        var output = RunStats($"{string.Join(" ", files)} --json");
        var doc = JsonDocument.Parse(output);

        var avg = doc.RootElement.GetProperty("avg_duration_seconds").GetDouble();
        Assert.Equal(2.0, avg, precision: 1); // (1000+2000+3000)/3 = 2000ms = 2s
    }

    [Fact]
    public void Stats_PassRate_CalculatesCorrectly()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", false, 100)
        });

        var output = RunStats($"{string.Join(" ", files)} --json");
        var doc = JsonDocument.Parse(output);
        var passRate = doc.RootElement.GetProperty("pass_rate").GetDouble();

        Assert.Equal(75.0, passRate, precision: 1); // 3/4 = 75%
    }

    // ========== Group-By Tests ==========

    [Fact]
    public void Stats_GroupByModel_GroupsCorrectly()
    {
        var files = CreateWazaFiles(new[] {
            ("gpt-4", true, 100),
            ("gpt-4", false, 150),
            ("claude-3", true, 200),
            ("claude-3", true, 250)
        });

        var output = RunStats($"{string.Join(" ", files)} --group-by model --json");
        var doc = JsonDocument.Parse(output);

        Assert.True(doc.RootElement.TryGetProperty("by_model", out var byModel));
        // by_model is an array of objects with "model", "count", "passed", "failed"
        var models = byModel.EnumerateArray()
            .ToDictionary(e => e.GetProperty("model").GetString()!, e => e);

        Assert.Contains("gpt-4", models.Keys);
        Assert.Contains("claude-3", models.Keys);

        Assert.Equal(2, models["gpt-4"].GetProperty("count").GetInt32());
        Assert.Equal(1, models["gpt-4"].GetProperty("passed").GetInt32());
        Assert.Equal(1, models["gpt-4"].GetProperty("failed").GetInt32());

        Assert.Equal(2, models["claude-3"].GetProperty("count").GetInt32());
        Assert.Equal(2, models["claude-3"].GetProperty("passed").GetInt32());
        Assert.Equal(0, models["claude-3"].GetProperty("failed").GetInt32());
    }

    [Fact]
    public void Stats_GroupByModel_PlainText_ShowsGroups()
    {
        var files = CreateWazaFiles(new[] {
            ("gpt-4", true, 100),
            ("claude-3", false, 150)
        });

        var output = RunStats($"{string.Join(" ", files)} --group-by model");

        Assert.Contains("gpt-4", output);
        Assert.Contains("claude-3", output);
    }

    [Fact]
    public void Stats_GroupByModel_CalculatesGroupPassRates()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", false, 100),
            ("model-b", true, 100)
        });

        var output = RunStats($"{string.Join(" ", files)} --group-by model --json");
        var doc = JsonDocument.Parse(output);
        var byModel = doc.RootElement.GetProperty("by_model");

        var models = byModel.EnumerateArray()
            .ToDictionary(e => e.GetProperty("model").GetString()!, e => e);

        var passRateA = models["model-a"].GetProperty("pass_rate").GetDouble();
        Assert.Equal(66.67, passRateA, precision: 0); // 2/3

        var passRateB = models["model-b"].GetProperty("pass_rate").GetDouble();
        Assert.Equal(100.0, passRateB, precision: 1);
    }

    // ========== Filter Tests ==========

    [Fact]
    public void Stats_FilterModel_IncludesOnlyMatchingFiles()
    {
        var files = CreateWazaFiles(new[] {
            ("gpt-4", true, 100),
            ("gpt-4", false, 150),
            ("claude-3", true, 200)
        });

        var output = RunStats($"{string.Join(" ", files)} --filter-model gpt-4 --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(2, total);
    }

    [Fact]
    public void Stats_FilterTask_IncludesOnlyMatchingFiles()
    {
        var files = CreateWazaFilesWithTask(new[] {
            ("model-a", "code-review", true, 100),
            ("model-a", "code-review", false, 150),
            ("model-b", "bug-fix", true, 200)
        });

        var output = RunStats($"{string.Join(" ", files)} --filter-task code-review --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(2, total);
    }

    [Fact]
    public void Stats_MultipleFilters_AppliesBoth()
    {
        var files = CreateWazaFilesWithTask(new[] {
            ("gpt-4", "code-review", true, 100),
            ("gpt-4", "bug-fix", false, 150),
            ("claude-3", "code-review", true, 200)
        });

        var output = RunStats($"{string.Join(" ", files)} --filter-model gpt-4 --filter-task code-review --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(1, total);
    }

    // ========== Edge Case Tests ==========

    [Fact]
    public void Stats_EmptyDirectory_ReportsZeroFiles()
    {
        var emptyDir = Path.Combine(TestDataDir, $"empty-{Guid.NewGuid()}");
        Directory.CreateDirectory(emptyDir);

        var pattern = Path.Combine(emptyDir, "*.json");
        var output = RunStats($"\"{pattern}\" --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(0, total);
    }

    [Fact]
    public void Stats_SingleFile_Works()
    {
        var files = CreateWazaFiles(new[] { ("model-a", true, 100) });

        var output = RunStats($"{files[0]} --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(1, total);
    }

    [Fact]
    public void Stats_MixedFormats_ProcessesValidFiles()
    {
        var wazaFiles = CreateWazaFiles(new[] { ("model-a", true, 100) });
        var copilotFile = CreateCopilotJsonlFile();

        var allFiles = wazaFiles.Concat(new[] { copilotFile }).ToArray();
        var output = RunStats($"{string.Join(" ", allFiles)} --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.True(total >= 1);
    }

    [Fact]
    public void Stats_MalformedFile_SkipsWithWarning()
    {
        var validFiles = CreateWazaFiles(new[] { ("model-a", true, 100) });
        var malformedFile = CreateMalformedJsonFile();

        var allFiles = validFiles.Concat(new[] { malformedFile }).ToArray();

        var exception = Record.Exception(() => RunStats(string.Join(" ", allFiles)));
        Assert.Null(exception);
    }

    [Fact]
    public void Stats_MissingModelField_HandledGracefully()
    {
        // File with transcript but no config.model_id — model will be empty string
        var files = CreateWazaFilesWithTask(new[] {
            ("test-model", "task1", true, 100),
        });
        // Also create a minimal transcript-only file (no model)
        var noModelFile = Path.Combine(TestDataDir, $"waza-nomodel-{Guid.NewGuid()}.json");
        File.WriteAllText(noModelFile, JsonSerializer.Serialize(new
        {
            transcript = new[] { new { role = "user", content = "hello" } },
            duration_ms = 150
        }));

        var allFiles = files.Concat(new[] { noModelFile }).ToArray();
        var output = RunStats($"{string.Join(" ", allFiles)} --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(2, total);
    }

    [Fact]
    public void Stats_NoMatchingFiles_ReportsZero()
    {
        var files = CreateWazaFiles(new[] { ("model-a", true, 100) });

        var output = RunStats($"{string.Join(" ", files)} --filter-model nonexistent --json");
        var doc = JsonDocument.Parse(output);

        var total = doc.RootElement.GetProperty("total_files").GetInt32();
        Assert.Equal(0, total);
    }

    // ========== CI Threshold Tests ==========

    [Fact]
    public void Stats_FailThreshold_BelowThreshold_ExitsWithError()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", false, 100),
            ("model-a", false, 100),
            ("model-a", false, 100)
        }); // 25% pass rate

        var exitCode = RunStatsWithExitCode($"{string.Join(" ", files)} --fail-threshold 80");

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void Stats_FailThreshold_AboveThreshold_ExitsSuccess()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", false, 100)
        }); // 75% pass rate

        var exitCode = RunStatsWithExitCode($"{string.Join(" ", files)} --fail-threshold 70");

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void Stats_FailThreshold_ExactThreshold_ExitsSuccess()
    {
        var files = CreateWazaFiles(new[] {
            ("model-a", true, 100),
            ("model-a", true, 100),
            ("model-a", false, 100),
            ("model-a", false, 100)
        }); // 50% pass rate

        var exitCode = RunStatsWithExitCode($"{string.Join(" ", files)} --fail-threshold 50");

        Assert.Equal(0, exitCode);
    }

    // ========== Helper Methods ==========

    /// <summary>
    /// Creates Waza "tasks" format files (EvaluationOutcome) with config.model_id,
    /// runs[0].validations for pass/fail, and runs[0].duration_ms.
    /// </summary>
    private string[] CreateWazaFiles((string model, bool passed, int durationMs)[] specs)
    {
        return CreateWazaFilesWithTask(
            specs.Select(s => (s.model, "test-task", s.passed, s.durationMs)).ToArray());
    }

    private string[] CreateWazaFilesWithTask((string model, string task, bool passed, int durationMs)[] specs)
    {
        var files = new List<string>();

        foreach (var spec in specs)
        {
            var filePath = Path.Combine(TestDataDir, $"waza-{Guid.NewGuid()}.json");
            var content = new
            {
                config = new { model_id = spec.model },
                tasks = new[] {
                    new {
                        display_name = spec.task,
                        test_id = $"t-{Guid.NewGuid():N}",
                        status = spec.passed ? "passed" : "failed",
                        runs = new[] {
                            new {
                                transcript = new[] {
                                    new { role = "user", content = "test prompt" },
                                    new { role = "assistant", content = "test response" }
                                },
                                duration_ms = spec.durationMs,
                                session_digest = new { total_turns = 2, tool_call_count = 0, tokens_in = 100, tokens_out = 50 },
                                validations = new Dictionary<string, object> {
                                    ["v1"] = new { score = spec.passed ? 1.0 : 0.0, passed = spec.passed, feedback = spec.passed ? "ok" : "fail" }
                                }
                            }
                        }
                    }
                },
                summary = new { AggregateScore = spec.passed ? 1.0 : 0.0, DurationMs = spec.durationMs }
            };

            File.WriteAllText(filePath, JsonSerializer.Serialize(content));
            files.Add(filePath);
        }

        return files.ToArray();
    }

    private string CreateCopilotJsonlFile()
    {
        var filePath = Path.Combine(TestDataDir, $"copilot-{Guid.NewGuid()}.jsonl");
        var events = new object[]
        {
            new { type = "session.start", timestamp = DateTimeOffset.UtcNow.ToString("o") },
            new { type = "user.message", timestamp = DateTimeOffset.UtcNow.ToString("o"), data = new { content = "test" } }
        };

        File.WriteAllLines(filePath, events.Select(e => JsonSerializer.Serialize(e)));
        return filePath;
    }

    private string CreateMalformedJsonFile()
    {
        var filePath = Path.Combine(TestDataDir, $"malformed-{Guid.NewGuid()}.json");
        File.WriteAllText(filePath, "{ invalid json content");
        return filePath;
    }

    private static readonly string ReplayCs = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "dotnet-replay.csproj"));

    private string RunStats(string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project {ReplayCs} -- stats {args}",
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

    private int RunStatsWithExitCode(string args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project {ReplayCs} -- stats {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ReplayCs)!
        };

        using var process = System.Diagnostics.Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start process");

        process.WaitForExit();
        return process.ExitCode;
    }
}
