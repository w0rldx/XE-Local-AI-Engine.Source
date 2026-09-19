namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Reflection;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Run-logger coverage. Proves that JSONL log files are created under the host-side
///     <c>logs/</c> directory, that every record carries the run-id/NodeId/OwnerUserId/providerName
///     correlation envelope, and that argument summaries never leak raw host paths or secrets.
///     Tests run against <see cref="AgentHomeRunLogger" /> directly — no sandbox, no Docker, no Ollama.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomeRunLoggerTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 5, day: 30, hour: 9, minute: 0, second: 0, TimeSpan.Zero);

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }


    [Test]
    public async Task OpenAsync_CreatesEventsJsonlWithStartedRecord()
    {
        var (logger, ctx) = CreateLogger();

        await logger.OpenAsync(ctx);

        var eventsFile = Path.Combine(ctx.HostLogDirectory, "events.jsonl");
        AssertEx.True(File.Exists(eventsFile), "events.jsonl must be created by OpenAsync");

        var record = ReadFirstRecord(eventsFile);
        AssertEx.Equal("started", record.GetProperty("eventName").GetString());
        AssertCorrelation(record, ctx);
    }

    [Test]
    public async Task OpenAsync_RecordTimestampMatchesTimeProvider()
    {
        var (logger, ctx) = CreateLogger();

        await logger.OpenAsync(ctx);

        var record = ReadFirstRecord(Path.Combine(ctx.HostLogDirectory, "events.jsonl"));
        var ts = record.GetProperty("timestampUtc").GetDateTimeOffset();
        AssertEx.Equal(FixedNow, ts);
    }


    [Test]
    public async Task AppendEventAsync_AppendsToEventsJsonlWithCorrelation()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        await logger.AppendEventAsync("run_completed", "exit=0");

        var lines = await File.ReadAllLinesAsync(Path.Combine(ctx.HostLogDirectory, "events.jsonl"));
        AssertEx.Equal(expected: 2, lines.Length - CountTrailingEmpty(lines)); // started + run_completed

        var completedRecord = ParseRecord(lines[1]);
        AssertEx.Equal("run_completed", completedRecord.GetProperty("eventName").GetString());
        AssertEx.Equal("exit=0", completedRecord.GetProperty("detail").GetString());
        AssertCorrelation(completedRecord, ctx);
    }

    [Test]
    public async Task AppendEventAsync_MultipleEventsAreAllAppended()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        await logger.AppendEventAsync("prepare_completed");
        await logger.AppendEventAsync("run_completed");
        await logger.AppendEventAsync("patch_exported");

        var lines = NonEmptyLines(await File.ReadAllLinesAsync(Path.Combine(ctx.HostLogDirectory, "events.jsonl")));
        AssertEx.Equal(expected: 4, lines.Count); // started + 3 more
    }


    [Test]
    public async Task AppendCommandAsync_CreatesCommandsJsonlWithRecord()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        var commandRecord = new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Node,
            TimestampUtc = FixedNow,
            ExecutionId = "run-001-cmd",
            Executable = "dotnet",
            Arguments = ["--version"],
            Completed = true,
            ExitCode = 0,
            DurationMs = 42
        };

        await logger.AppendCommandAsync(commandRecord);

        var commandsFile = Path.Combine(ctx.HostLogDirectory, "commands.jsonl");
        AssertEx.True(File.Exists(commandsFile), "commands.jsonl must be created by AppendCommandAsync");

        var record = ReadFirstRecord(commandsFile);
        AssertEx.Equal("dotnet", record.GetProperty("executable").GetString());
        AssertEx.Equal(expected: 0, record.GetProperty("exitCode").GetInt32());
        AssertEx.Equal(expected: 42L, record.GetProperty("durationMs").GetInt64());
        AssertCorrelation(record, ctx);
    }

    /// <summary>
    ///     WHO ran a command is the point of this log: the node's own export git runs in the same sandbox, over the
    ///     same workspace, as the model's <c>run_command</c>. Asserted off DISK, because the in-memory record was
    ///     always right while the serialized line silently dropped the actor.
    /// </summary>
    [Test]
    public async Task AppendCommandAsync_WritesTheActorOfEveryRecordToDisk()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        await logger.AppendCommandAsync(new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Model,
            TimestampUtc = FixedNow,
            ExecutionId = "run-001-cmd",
            Executable = "/bin/echo",
            Arguments = ["hello"],
            Completed = true,
            ExitCode = 0,
            DurationMs = 7
        });
        await logger.AppendCommandAsync(new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Node,
            TimestampUtc = FixedNow,
            ExecutionId = "run-001-export-diff",
            Executable = "git",
            Arguments = ["diff", "--no-textconv"],
            Completed = true,
            ExitCode = 0,
            DurationMs = 11
        });

        var lines = NonEmptyLines(await File.ReadAllLinesAsync(Path.Combine(ctx.HostLogDirectory, "commands.jsonl")));
        AssertEx.Equal(expected: 2, lines.Count);

        var model = ParseRecord(lines[0]);
        AssertEx.Equal(AgentHomeCommandActors.Model, model.GetProperty("actor").GetString(),
            "the model's own run_command must be attributed to the model on disk");
        AssertEx.Equal("/bin/echo", model.GetProperty("executable").GetString());
        AssertEx.Equal("hello", model.GetProperty("arguments")[0].GetString());
        AssertCorrelation(model, ctx);

        var node = ParseRecord(lines[1]);
        AssertEx.Equal(AgentHomeCommandActors.Node, node.GetProperty("actor").GetString(),
            "the node's own export git must be attributed to the node on disk");
        AssertEx.Equal("git", node.GetProperty("executable").GetString());
        AssertEx.Equal("--no-textconv", node.GetProperty("arguments")[1].GetString());
        AssertEx.Equal(expected: 11L, node.GetProperty("durationMs").GetInt64());
        AssertCorrelation(node, ctx);
    }

    /// <summary>
    ///     The class of bug the missing actor was: the envelope is hand-written, so a member of
    ///     <see cref="AgentHomeCommandLogRecord" /> reaches disk only while someone remembers to list it there. Every
    ///     public member must appear in the serialized line, or be named on the allow-list below with a reason.
    /// </summary>
    [Test]
    public async Task AppendCommandAsync_SerializesEveryMemberOfTheRecord()
    {
        // Members deliberately kept OUT of commands.jsonl. Empty today; anything added here needs its reason written
        // beside it, because the alternative reading of an absent field is that it was dropped by accident.
        var deliberatelyOmitted = new HashSet<string>(StringComparer.Ordinal);

        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        // Every member non-null on purpose: the logger drops nulls, so a record with the usual null ErrorClass would
        // let a member the envelope forgot pass as one the serializer correctly omitted.
        await logger.AppendCommandAsync(new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Node,
            TimestampUtc = FixedNow,
            ExecutionId = "run-001-cmd",
            Executable = "git",
            Arguments = ["diff"],
            Completed = false,
            ExitCode = 1,
            DurationMs = 3,
            ErrorClass = nameof(TimeoutException)
        });

        var record = ReadFirstRecord(Path.Combine(ctx.HostLogDirectory, "commands.jsonl"));

        foreach (var property in typeof(AgentHomeCommandLogRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (deliberatelyOmitted.Contains(property.Name))
            {
                continue;
            }

            // JsonSerializerDefaults.Web: the envelope's member names are camelCased on the way out.
            var jsonName = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            AssertEx.True(record.TryGetProperty(jsonName, out _),
                $"AgentHomeCommandLogRecord.{property.Name} never reaches commands.jsonl as '{jsonName}' — list it in the envelope "
                + "in AgentHomeRunLogger.AppendCommandAsync, or on this test's deliberatelyOmitted allow-list with a reason");
        }
    }

    [Test]
    public async Task AppendCommandAsync_RecordDoesNotContainRawHostPath()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        // Arguments are sanitised by the caller; we pass only model-safe strings.
        var commandRecord = new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Node,
            TimestampUtc = FixedNow,
            ExecutionId = "run-001-cmd",
            Executable = "dotnet",
            Arguments = ["build", "runs/run-001/workspace"], // run-relative, not a host path
            Completed = true,
            ExitCode = 0,
            DurationMs = 100
        };

        await logger.AppendCommandAsync(commandRecord);

        var raw = await File.ReadAllTextAsync(Path.Combine(ctx.HostLogDirectory, "commands.jsonl"));

        // The host log directory root must not appear in the log file.
        AssertEx.False(raw.Contains(ctx.HostLogDirectory, StringComparison.Ordinal),
            "commands.jsonl must not contain the raw host log directory path");
    }


    [Test]
    public async Task AppendToolCallAsync_CreatesToolCallsJsonlWithRecord()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        var toolRecord = new AgentHomeToolCallLogRecord
        {
            TimestampUtc = FixedNow,
            RunId = ctx.RunId,
            ToolName = "run_in_agent_home",
            Location = "ClientLocal",
            ApprovalId = "approval_abc",
            Status = "started",
            ArgumentSummary = new
            {
                selectedFolderIds = new[]
                {
                    "folder-123"
                },
                allowedActions = new[]
                {
                    "read_workspace"
                }
            },
            RedactionApplied = true
        };

        await logger.AppendToolCallAsync(toolRecord);

        var toolCallsFile = Path.Combine(ctx.HostLogDirectory, "tool-calls.jsonl");
        AssertEx.True(File.Exists(toolCallsFile), "tool-calls.jsonl must be created by AppendToolCallAsync");

        var record = ReadFirstRecord(toolCallsFile);
        AssertEx.Equal("run_in_agent_home", record.GetProperty("toolName").GetString());
        AssertEx.Equal("ClientLocal", record.GetProperty("location").GetString());
        AssertEx.Equal("started", record.GetProperty("status").GetString());
        AssertEx.True(record.GetProperty("redactionApplied").GetBoolean(), "redactionApplied must be true");
        AssertCorrelation(record, ctx);
    }

    [Test]
    public async Task AppendToolCallAsync_ArgumentSummaryDoesNotContainRawHostPath()
    {
        var hostRoot = "/home/user/secret/host/path";
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        // The caller has already redacted: the summary contains only run-relative identifiers.
        var toolRecord = new AgentHomeToolCallLogRecord
        {
            TimestampUtc = FixedNow,
            RunId = ctx.RunId,
            ToolName = "run_in_agent_home",
            Location = "ClientLocal",
            Status = "succeeded",
            ArgumentSummary = new
            {
                selectedFolderIds = new[]
                {
                    "folder-123"
                }
            },
            RedactionApplied = true
        };

        await logger.AppendToolCallAsync(toolRecord);

        var raw = await File.ReadAllTextAsync(Path.Combine(ctx.HostLogDirectory, "tool-calls.jsonl"));
        AssertEx.False(raw.Contains(hostRoot, StringComparison.Ordinal),
            "tool-calls.jsonl must not contain raw host paths");
    }

    [Test]
    public async Task AppendToolCallAsync_RunIdCorrelationPresentOnRecord()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        var toolRecord = new AgentHomeToolCallLogRecord
        {
            TimestampUtc = FixedNow,
            RunId = ctx.RunId,
            ToolName = "run_in_agent_home",
            Location = "ClientLocal",
            Status = "succeeded",
            RedactionApplied = false
        };

        await logger.AppendToolCallAsync(toolRecord);

        var record = ReadFirstRecord(Path.Combine(ctx.HostLogDirectory, "tool-calls.jsonl"));
        AssertEx.Equal(ctx.RunId, record.GetProperty("runId").GetString());
    }


    [Test]
    public async Task AllLogFiles_ContainFullCorrelationEnvelope()
    {
        var (logger, ctx) = CreateLogger();
        await logger.OpenAsync(ctx);

        await logger.AppendEventAsync("run_completed");
        await logger.AppendCommandAsync(new AgentHomeCommandLogRecord
        {
            Actor = AgentHomeCommandActors.Node,
            TimestampUtc = FixedNow,
            ExecutionId = "run-99-cmd",
            Executable = "dotnet",
            Arguments = ["--version"],
            Completed = true,
            ExitCode = 0,
            DurationMs = 10
        });
        await logger.AppendToolCallAsync(new AgentHomeToolCallLogRecord
        {
            TimestampUtc = FixedNow,
            RunId = ctx.RunId,
            ToolName = "run_in_agent_home",
            Location = "ClientLocal",
            Status = "succeeded",
            RedactionApplied = false
        });

        var logDir = ctx.HostLogDirectory;
        foreach (var file in new[]
                 {
                     "events.jsonl",
                     "commands.jsonl",
                     "tool-calls.jsonl"
                 })
        {
            var lines = NonEmptyLines(await File.ReadAllLinesAsync(Path.Combine(logDir, file)));
            foreach (var line in lines)
            {
                var record = ParseRecord(line);
                AssertCorrelationFields(record, ctx, file);
            }
        }
    }


    [Test]
    public async Task AppendEventAsync_BeforeOpen_Throws()
    {
        var logger = new AgentHomeRunLogger(new FixedClock(FixedNow));

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            logger.AppendEventAsync("run_completed"));
    }


    private (AgentHomeRunLogger Logger, AgentHomeRunLogContext Context) CreateLogger()
    {
        var logDir = NewTempDir();
        var ctx = new AgentHomeRunLogContext
        {
            RunId = "run-test-" + Guid.NewGuid().ToString("N")[..8],
            HostLogDirectory = logDir,
            NodeId = "node-abc",
            OwnerUserId = "owner-xyz",
            ProviderName = "fake"
        };
        return (new AgentHomeRunLogger(new FixedClock(FixedNow)), ctx);
    }

    private static JsonElement ReadFirstRecord(string filePath)
    {
        var firstLine = File.ReadLines(filePath).First(line => !string.IsNullOrWhiteSpace(line));
        return ParseRecord(firstLine);
    }

    private static JsonElement ParseRecord(string line)
    {
        return JsonDocument.Parse(line).RootElement;
    }

    private static void AssertCorrelation(JsonElement record, AgentHomeRunLogContext ctx)
    {
        AssertCorrelationFields(record, ctx, fileName: null);
    }

    private static void AssertCorrelationFields(JsonElement record, AgentHomeRunLogContext ctx, string? fileName)
    {
        var label = fileName is null ? string.Empty : $" in {fileName}";

        AssertEx.Equal(ctx.NodeId, record.GetProperty("nodeId").GetString(),
            $"nodeId correlation missing{label}");
        AssertEx.Equal(ctx.OwnerUserId, record.GetProperty("ownerUserId").GetString(),
            $"ownerUserId correlation missing{label}");
        AssertEx.Equal(ctx.ProviderName, record.GetProperty("providerName").GetString(),
            $"providerName correlation missing{label}");
    }

    private static int CountTrailingEmpty(string[] lines)
    {
        var count = 0;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private static List<string> NonEmptyLines(string[] lines)
    {
        return lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agenthome-logger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
