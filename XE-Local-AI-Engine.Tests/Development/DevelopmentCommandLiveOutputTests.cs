namespace XE_Local_AI_Engine.Tests.Development;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     A Development command's live output and stop: redacted batches tagged with command and execution id, a stop by that id, and
///     a flood that cannot evict a lifecycle update.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevelopmentCommandLiveOutputTests : IDisposable
{
    private const string BaseCommit = "0123456789abcdef0123456789abcdef01234567";

    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-live-output-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new();
    private readonly DevelopmentExecutionSnapshot _snapshot = Snapshot();
    private readonly FakeSandboxRuntimeProvider _sandbox;
    private readonly DevelopmentAttemptLiveBroker _broker;

    public DevelopmentCommandLiveOutputTests()
    {
        _sandbox = new FakeSandboxRuntimeProvider(_clock);
        _broker = new DevelopmentAttemptLiveBroker(Options.Create(OptionsValue()), _clock);
        AssertEx.True(_broker.Register(_snapshot.AttemptId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task CatalogCommand_WithLiveProgress_PublishesRedactedOutputTaggedWithCommandAndExecutionId()
    {
        var session = await CreateSessionAsync();
        var stdout = $"secret: hunter2\nbuilt {session.HostWorktreePath}/bin/out.dll\n";
        _sandbox.RegisterCommand(CommandLine(DevelopmentCommandIds.GitStatus), exitCode: 0, stdout, "warn line\n");
        var tools = CreateTools(session, Live());

        var result = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        // Observe-only: the model-visible result and the evidence still carry the raw output.
        AssertEx.Contains(result, "secret: hunter2");
        AssertEx.Equal(stdout, tools.CommandEvidence.Single().StandardOutput);
        var executionId = _sandbox.ExecutedCommands.Single(command => command.ExecutionId.StartsWith(DevelopmentCommandIds.GitStatus + "-", StringComparison.Ordinal))
                                  .ExecutionId;
        var updates = Drain();
        var started = updates.First(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command);
        var output = updates.Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.CommandOutput);
        var completed = updates.Last(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command);
        AssertEx.True(updates.IndexOf(started) < updates.IndexOf(output) && updates.IndexOf(output) < updates.IndexOf(completed));
        foreach (var update in new[] { started, output, completed })
        {
            AssertEx.Equal(DevelopmentCommandIds.GitStatus, update.CurrentCommandId);
            AssertEx.Equal(executionId, update.CurrentCommandExecutionId);
        }

        // The workspace root goes first, then the live sanitizer turns any remaining absolute path into [path].
        AssertEx.Equal("secret[redacted]\nbuilt <workspace>[path]\nwarn line\n", output.OutputDelta);
        AssertEx.Equal(expected: 0, output.CommandOutputSkippedLines);
        AssertEx.Equal(expected: 0, completed.CommandOutputSkippedLines);
    }

    [Test]
    public async Task CatalogCommand_WhenOutputOutrunsOneBatch_CountsTheSkippedLinesOnTheCompletedUpdate()
    {
        var session = await CreateSessionAsync();
        var lines = string.Concat(Enumerable.Range(0, 500).Select(static index => $"line {index:D4} of a verbose build\n"));
        _sandbox.RegisterCommand(CommandLine(DevelopmentCommandIds.GitStatus), exitCode: 0, lines);
        var tools = CreateTools(session, Live());

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        var updates = Drain();
        var output = updates.Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.CommandOutput);
        var completed = updates.Last(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command);
        AssertEx.True(output.OutputDelta!.Length <= 2048, "one batch stays within half the live text cap");
        AssertEx.True(completed.CommandOutputSkippedLines > 0);
        AssertEx.Equal(500, output.OutputDelta.Count(static character => character == '\n') + completed.CommandOutputSkippedLines!.Value);
    }

    [Test]
    public async Task CatalogCommand_WithoutLiveProgress_AttachesNoObserver()
    {
        var session = await CreateSessionAsync();
        _sandbox.RegisterCommand(CommandLine(DevelopmentCommandIds.GitStatus), exitCode: 0, "clean\n");
        var tools = CreateTools(session, liveProgress: null);

        var result = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        AssertEx.Equal("git_status: exit=0, completed=True\nclean\n\n", result);
        AssertEx.True(_sandbox.ExecutedCommands.All(static command => command.OutputObserver is null));
    }

    [Test]
    public async Task StopCommand_ByExecutionId_CancelsOnlyThatAttemptsCommand_AndTheEvidenceIsNotCompleted()
    {
        var session = await CreateSessionAsync();
        _sandbox.RegisterBlockingCommand(CommandLine(DevelopmentCommandIds.GitStatus));
        var otherAttemptId = Guid.NewGuid();
        AssertEx.True(_broker.Register(otherAttemptId));
        var tools = CreateTools(session, Live());

        // Everything up to the blocking await runs synchronously, so the command is tracked once the call returns.
        var running = tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);
        var executionId = Drain().Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command).CurrentCommandExecutionId!;

        AssertEx.False(await _broker.TryStopCommandAsync(otherAttemptId, executionId), "another attempt cannot stop this command");
        AssertEx.False(await _broker.TryStopCommandAsync(_snapshot.AttemptId, "git_status-unknown"));
        AssertEx.False(running.IsCompleted);
        AssertEx.True(await _broker.TryStopCommandAsync(_snapshot.AttemptId, executionId));
        var result = await running;

        // The model, the evidence and the live view all read the operator's stop, never a timeout or a generic cancel.
        AssertEx.Equal($"git_status: exit=-1, completed=False\n\n{DevelopmentCommandEvidence.StoppedByOperatorMessage}", result);
        var evidence = tools.CommandEvidence.Single();
        AssertEx.False(evidence.Completed);
        AssertEx.True(evidence.StoppedByOperator);
        AssertEx.False(await _broker.TryStopCommandAsync(_snapshot.AttemptId, executionId), "a finished command is no longer stoppable");
        var completed = Drain().Last(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command);
        AssertEx.Equal(executionId, completed.CurrentCommandExecutionId);
        AssertEx.Equal("Command git_status was stopped by the operator.", completed.CurrentActivity);
    }

    [Test]
    public async Task StopCommand_KeepsTheOutputPrintedBeforeTheStop_AndOnlyTheCancelLineReadsAsTheOperatorStop()
    {
        var session = await CreateSessionAsync();
        _sandbox.RegisterBlockingCommand(CommandLine(DevelopmentCommandIds.GitStatus), standardOutput: "M a.txt\n", standardError: "warn-1\n");
        var tools = CreateTools(session, Live());

        var running = tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);
        var executionId = Drain().Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command).CurrentCommandExecutionId!;
        AssertEx.True(await _broker.TryStopCommandAsync(_snapshot.AttemptId, executionId));
        var result = await running;

        // The model and the evidence see how far the command got.
        AssertEx.Equal($"git_status: exit=-1, completed=False\nM a.txt\n\nwarn-1\n{DevelopmentCommandEvidence.StoppedByOperatorMessage}", result);
        var evidence = tools.CommandEvidence.Single();
        AssertEx.True(evidence.StoppedByOperator);
        AssertEx.Equal("M a.txt\n", evidence.StandardOutput);
        AssertEx.Equal($"warn-1\n{DevelopmentCommandEvidence.StoppedByOperatorMessage}", evidence.StandardError);
    }

    [Test]
    public async Task StopCommand_WithStderrPastTheEvidenceCap_StillEndsWithTheOperatorStop_WithinTheCap()
    {
        const int Cap = 200;
        var session = await CreateSessionAsync();
        _sandbox.RegisterBlockingCommand(CommandLine(DevelopmentCommandIds.GitStatus), standardOutput: "M a.txt\n", standardError: new string('w', 1000) + "\n");
        var tools = CreateTools(session,
            Live(),
            new DevelopmentOptions
            {
                Enabled = true,
                MaxAttemptDurationSeconds = 60,
                LiveChannelCapacity = 64,
                MaxCommandOutputBytes = Cap
            });

        var running = tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);
        var executionId = Drain().Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command).CurrentCommandExecutionId!;
        AssertEx.True(await _broker.TryStopCommandAsync(_snapshot.AttemptId, executionId));
        var result = await running;

        // The cap keeps the head, so without reserved room the stop line would be the part cut off.
        const string Header = "git_status: exit=-1, completed=False\nM a.txt\n\n";
        AssertEx.True(result.StartsWith(Header, StringComparison.Ordinal), result);
        var renderedStderr = result[Header.Length..];
        AssertEx.True(renderedStderr.EndsWith(DevelopmentCommandEvidence.StoppedByOperatorMessage, StringComparison.Ordinal), renderedStderr);
        AssertEx.Equal(expected: Cap, Encoding.UTF8.GetByteCount(renderedStderr), "the printed stderr fills the room the stop line leaves");
        var evidence = tools.CommandEvidence.Single();
        AssertEx.Equal(renderedStderr, evidence.StandardError);
        AssertEx.True(evidence.OutputTruncated, "the printed stderr was cut");
        AssertEx.True(evidence.StoppedByOperator);
    }

    [Test]
    public async Task StopCommand_WithACapBelowTheStopLine_CutsTheStopLineToTheCap_AndReadsTruncated()
    {
        // Below the 54-byte stop line, yet above a 41-byte commit id, so the worktree invariant probes still read whole.
        const int Cap = 50;
        var session = await CreateSessionAsync();
        _sandbox.RegisterBlockingCommand(CommandLine(DevelopmentCommandIds.GitStatus));
        var tools = CreateTools(session,
            Live(),
            new DevelopmentOptions
            {
                Enabled = true,
                MaxAttemptDurationSeconds = 60,
                LiveChannelCapacity = 64,
                MaxCommandOutputBytes = Cap
            });

        var running = tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);
        var executionId = Drain().Single(update => update.Kind == DevelopmentAttemptLiveUpdateKind.Command).CurrentCommandExecutionId!;
        AssertEx.True(await _broker.TryStopCommandAsync(_snapshot.AttemptId, executionId));
        _ = await running;

        var evidence = tools.CommandEvidence.Single();
        AssertEx.Equal(DevelopmentCommandEvidence.StoppedByOperatorMessage[..Cap], evidence.StandardError);
        AssertEx.True(evidence.OutputTruncated, "a stop line cut to the cap reads truncated");
        AssertEx.True(evidence.StoppedByOperator);
    }

    [Test]
    [Arguments("Authorization: Bearer abc123")]
    [Arguments("{\"Authorization\": \"Basic abc123\"}")]
    [Arguments("{\"GITHUB_TOKEN\": \"abc123\"}")]
    [Arguments("Bearer abc123")]
    [Arguments("GITHUB_TOKEN=abc123")]
    [Arguments("AWS_SECRET_ACCESS_KEY=abc123")]
    [Arguments("token=abc123")]
    [Arguments("git clone https://user:abc123@github.com/o/r")]
    [Arguments("curl -H \"Authorization: Bearer abc123\" https://api.example")]
    [Arguments("Authorization: \"Basic x abc123\"")]
    [Arguments("password=\"correct horse abc123 staple\"")]
    [Arguments("DB_PASSWORD='a b;abc123'")]
    [Arguments("{\"api_key\": \"x, abc123\"}")]
    [Arguments("password=\"a\\\"b abc123\"")]
    [Arguments("password=\"unterminated abc123")]
    public void LiveSanitizer_RedactsEveryCredentialValue(string text)
    {
        // The secret is what must go; the sanitizer's own path rule may fold a redacted URL into [path] as well.
        var delta = SanitizedDelta(text);
        AssertEx.False(delta.Contains("abc123", StringComparison.Ordinal), delta);
    }

    [Test]
    public void LiveSanitizer_RedactsEveryLabelFormTheFormerDevelopmentPatternCovered()
    {
        // The labels and separators the sanitizer's own pattern matched before the shared helper replaced it; none may leak again.
        string[] labels = ["password", "passwd", "secret", "api_key", "api-key", "apikey", "authorization", "bearer", "PASSWORD", "Bearer", "KEY", "TOKEN_VALUE", "CREDENTIALS"];
        string[] separators = ["=", ":", ": "];
        var leaked = new List<string>();
        foreach (var text in labels.SelectMany(label => separators.Select(separator => label + separator + "abc123")).Append("Bearer abc123"))
        {
            if (SanitizedDelta(text).Contains("abc123", StringComparison.Ordinal))
            {
                leaked.Add(text);
            }
        }

        AssertEx.Empty(leaked, "every former label form keeps its value out of the live view");
    }

    [Test]
    public void LiveSanitizer_KeepsAQuotedNonSecret_AndStopsAnUnterminatedQuoteAtItsLine()
    {
        AssertEx.Equal("name=\"hello, world\"; size: 3", SanitizedDelta("name=\"hello, world\"; size: 3"));
        AssertEx.Equal("password[redacted]\nbuild ok", SanitizedDelta("password=\"abc def\nbuild ok"));
    }

    private static string SanitizedDelta(string text)
    {
        var sanitized = DevelopmentAttemptLiveSanitizer.Sanitize(new DevelopmentAttemptLiveUpdate
            {
                ProjectId = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                Kind = DevelopmentAttemptLiveUpdateKind.CommandOutput,
                Role = DevelopmentAttemptRole.Coder,
                Status = PersistenceDevelopmentAttemptStatus.Running,
                ModelId = "coder-model",
                Provider = "local",
                OutputDelta = text
            },
            maxTextCharacters: 4096);
        return AssertEx.NotNull(sanitized.OutputDelta);
    }

    [Test]
    public void ValidationVerdict_ForAStoppedCommand_SaysTheOperatorStoppedIt()
    {
        var stopped = new DevelopmentCommandEvidence(DevelopmentCommandIds.GitDiffCheck,
            ExitCode: -1,
            Completed: false,
            OutputTruncated: false,
            DurationMilliseconds: 5,
            StandardOutput: string.Empty,
            StandardError: DevelopmentCommandEvidence.StoppedByOperatorMessage)
        {
            StoppedByOperator = true
        };
        var timedOut = stopped with
        {
            StandardError = "Command timed out.",
            StoppedByOperator = false
        };
        // A repository command can print the stop line itself; without the flag from a stop request it is not a stop.
        var forged = stopped with
        {
            StoppedByOperator = false
        };

        var verdict = DevelopmentValidationVerdict.Evaluate(GenericProfile, [DevelopmentArtifactSanitizer.Sanitize(stopped)]);

        AssertEx.Equal(DevelopmentValidationFailureCodes.CommandStopped, verdict.FailureCode);
        AssertEx.Contains(verdict.FailureDetail!, "stopped by the operator");
        AssertEx.Equal(DevelopmentValidationFailureCodes.CommandDidNotComplete,
            DevelopmentValidationVerdict.Evaluate(GenericProfile, [timedOut]).FailureCode);
        AssertEx.Equal(DevelopmentValidationFailureCodes.CommandDidNotComplete,
            DevelopmentValidationVerdict.Evaluate(GenericProfile, [forged]).FailureCode);
    }

    [Test]
    public async Task StopCommand_WhenTheSandboxIsAlreadyGone_ReportsNothingToStopInsteadOfThrowing()
    {
        var session = await CreateSessionAsync();
        await _sandbox.KillAsync(session.SandboxHandle);
        AssertEx.True(_broker.TrackCommand(_snapshot.AttemptId,
            "git_status-raced",
            token => _sandbox.CancelCommandAsync(session.SandboxHandle, "git_status-raced", token)));

        AssertEx.False(await _broker.TryStopCommandAsync(_snapshot.AttemptId, "git_status-raced"));
    }

    [Test]
    public void Flood_DropsCommandOutputButNeverALifecycleOrTerminalUpdate()
    {
        AssertEx.True(_broker.TryPublish(Update(DevelopmentAttemptLiveUpdateKind.Command, "started")));
        for (var index = 0; index < 1000; index++)
        {
            _ = _broker.TryPublish(Update(DevelopmentAttemptLiveUpdateKind.CommandOutput, "line\n"));
        }

        AssertEx.True(_broker.TryPublish(Update(DevelopmentAttemptLiveUpdateKind.Command, "completed")));
        AssertEx.True(_broker.TryPublish(Update(DevelopmentAttemptLiveUpdateKind.Terminal, "terminal")));
        AssertEx.True(_broker.TryGetSnapshot(_snapshot.AttemptId, out var live));
        var updates = Drain();

        var lifecycle = updates.Where(static update => update.Kind != DevelopmentAttemptLiveUpdateKind.CommandOutput)
                               .Select(static update => update.CurrentActivity);
        AssertEx.Equal("started|completed|terminal", string.Join('|', lifecycle));
        var delivered = updates.Count(static update => update.Kind == DevelopmentAttemptLiveUpdateKind.CommandOutput);
        AssertEx.Equal(31, delivered);
        AssertEx.Equal(1000L - delivered, live.DroppedOrCoalescedUpdateCount);
    }

    private DevelopmentAttemptLiveUpdate Update(DevelopmentAttemptLiveUpdateKind kind, string activity) =>
        new()
        {
            ProjectId = _snapshot.ProjectId,
            TaskId = _snapshot.TaskId,
            AttemptId = _snapshot.AttemptId,
            Kind = kind,
            Role = DevelopmentAttemptRole.Coder,
            Status = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "local-model",
            Provider = "local",
            CurrentActivity = activity,
            OutputDelta = kind == DevelopmentAttemptLiveUpdateKind.CommandOutput ? activity : null
        };

    private List<DevelopmentAttemptLiveUpdate> Drain()
    {
        AssertEx.True(_broker.TryGetDeliveryReader(_snapshot.AttemptId, out var reader));
        var updates = new List<DevelopmentAttemptLiveUpdate>();
        while (reader!.TryRead(out var update))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string CommandLine(string commandId)
    {
        var command = GenericProfile.ResolveCommand(commandId);
        return command.Executable + " " + string.Join(" ", command.Arguments);
    }

    private DevelopmentAttemptLiveProgress Live() =>
        new(_snapshot, _broker, Options.Create(OptionsValue()), _clock, maxOutputTokens: 2048, maxToolCalls: 16);

    private DevelopmentWorkspaceTools CreateTools(DevelopmentWorkspaceSession session,
        DevelopmentAttemptLiveProgress? liveProgress,
        DevelopmentOptions? options = null) =>
        new(_sandbox, session, Options.Create(options ?? OptionsValue()), GenericProfile, liveProgress);

    /// <summary>A fake sandbox session whose identity mount covers the worktree and runtime directories, with the invariant probes scripted green.</summary>
    private async Task<DevelopmentWorkspaceSession> CreateSessionAsync()
    {
        var worktree = Path.Combine(_root, "worktree");
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(runtime);
        var handle = await _sandbox.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = FakeSandboxRuntimeProvider.Name,
                RuntimeProfile = "development",
                ManifestVersion = 1
            },
            RuntimeProfile = "development",
            Mounts =
            [
                new SandboxMount
                {
                    HostPath = _root,
                    SandboxPath = _root,
                    ReadOnly = false
                }
            ]
        });
        _sandbox.RegisterCommand("git " + string.Join(" ", AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}")), exitCode: 0, BaseCommit + "\n");
        _sandbox.RegisterCommand("git " + string.Join(" ", AgentHomeGit.Arguments("symbolic-ref", "--quiet", "HEAD")), exitCode: 1);
        return new DevelopmentWorkspaceSession
        {
            ProjectId = _snapshot.ProjectId,
            TaskId = _snapshot.TaskId,
            AttemptId = _snapshot.AttemptId,
            BaseCommit = BaseCommit,
            RepositoryIdentityHash = "identity",
            HostWorktreePath = worktree,
            RuntimePath = runtime,
            TrustedGitDirectory = Path.Combine(worktree, ".git"),
            SandboxHandle = handle
        };
    }

    private static DevelopmentOptions OptionsValue() =>
        new()
        {
            Enabled = true,
            MaxAttemptDurationSeconds = 60,
            LiveChannelCapacity = 64
        };

    private static DevelopmentExecutionSnapshot Snapshot() =>
        new()
        {
            ProjectId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            SelectedFolderId = Guid.NewGuid(),
            RepositoryIdentityHash = "identity",
            BaseBranch = "main",
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = 1,
            MaxTokens = 2048,
            MaxDurationSeconds = 60,
            Title = "Live output",
            Requirements = "Stream command output.",
            AcceptanceCriteriaJson = "[]",
            TaskStatus = DevelopmentTaskStatus.InProgress,
            TaskVersion = 1,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "local-model",
            Provider = "local",
            AttemptVersion = 1,
            CommandProfileJson = null
        };
}
