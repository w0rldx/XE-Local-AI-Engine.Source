namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Development;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     The P2 Phase 0 spike: can a development-workflow node-run get a prepared, command-running sandbox workspace
///     without a <c>DevelopmentTask</c> row behind it?
///     <para>
///         It is a go/no-go for the Tool and DevTask node executors, so it is written as evidence rather than as a
///         regression guard: one test proves the old path really did fail without those rows, and one proves the whole
///         chain — prepare, then run a profile command — now works from a bare <c>(projectId, nodeRunId)</c> pair.
///     </para>
/// </summary>
public sealed class DevWorkflowWorkspaceSecretsSinkTests : IDisposable
{
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-workflow-spike-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>
    ///     The blocker the seam exists for, asserted against the real store so it cannot be argued away. Dev Mode's sink
    ///     resolves the project from a task row before it does anything else, so a node-run's own ids — which name no
    ///     such row — never get past that <c>SingleAsync</c>. Everything else in <c>PrepareAsync</c> already reads the
    ///     snapshot as a value bag.
    /// </summary>
    [Test]
    public async Task DevModeSink_ForIdsThatNameNoTaskOrAttempt_IsRefusedByTheStore()
    {
        await using var factory = new TestServerWebAppFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var sink = new DevelopmentStoreWorkspaceSecretsSink(scope.ServiceProvider.GetRequiredService<IDevelopmentStore>());

        // The TYPE is the contract; the message is the framework's and could be reworded by any runtime update.
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => sink.RecordAsync(Guid.NewGuid(), Guid.NewGuid(), [".env"]))
                          .ConfigureAwait(false);
    }

    /// <summary>Dev Mode's own behaviour, forwarded verbatim: the keys and the paths reach the store unchanged.</summary>
    [Test]
    public async Task DevModeSink_ForwardsItsKeysAndPathsToTheStoreUnchanged()
    {
        var store = Substitute.For<IDevelopmentStore>();
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await new DevelopmentStoreWorkspaceSecretsSink(store).RecordAsync(taskId, attemptId, [".env"]).ConfigureAwait(false);

        _ = store.Received(1).RecordWorkspaceSecretsAsync(taskId,
            attemptId,
            Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 1 && paths[0] == ".env"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The gate. A synthesized snapshot keyed on a node-run id prepares a real workspace and runs a real profile
    ///     command, and the committed credential it carries is reported to the workflow's sink instead of to a task row
    ///     that does not exist.
    ///     <para>
    ///         The repository deliberately commits a <c>.env</c>: without one, <c>PrepareAsync</c> never reaches the
    ///         report at all and the test would prove nothing about the blocker.
    ///     </para>
    /// </summary>
    [Test]
    public async Task PrepareAsync_ForABareProjectAndNodeRunPair_PreparesTheWorkspaceAndRunsAProfileCommand()
    {
        var projectId = Guid.NewGuid();
        var nodeRunId = Guid.NewGuid();
        var sink = new RecordingWorkspaceSecretsSink();

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository));

        // TaskId and AttemptId ARE the node-run id: for a workflow node-run there is nothing else to key the workspace
        // on, and nothing behind either value in the Development tables.
        var snapshot = NodeRunSnapshot(projectId, nodeRunId, identity);

        using var sandbox = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, sink);

        var session = await provider.PrepareAsync(snapshot, new DevelopmentRepositoryBinding(projectId, snapshot.SelectedFolderId!.Value, "repository", repository, identity))
                                    .ConfigureAwait(false);

        AssertEx.True(Directory.Exists(session.HostWorktreePath), session.HostWorktreePath);
        AssertEx.Equal(expected: 1, sink.Recorded.Count, "a committed credential must reach the sink, or this test never exercised the blocked call.");
        AssertEx.Equal(nodeRunId, sink.Recorded[0].IsolationKey);
        AssertEx.Equal(nodeRunId, sink.Recorded[0].AttemptKey);
        AssertEx.Equal(".env", sink.Recorded[0].Paths[0]);

        var tools = new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile);
        var status = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);

        AssertEx.NotNull(status);
        AssertEx.Equal(expected: 1, tools.CommandEvidence.Count);
        AssertEx.Equal(expected: 0, tools.CommandEvidence[0].ExitCode, tools.CommandEvidence[0].StandardError);
    }

    private static DevelopmentOptions OptionsValue() =>
        new()
        {
            Enabled = true,
            MaxArtifactBytes = 2 * 1024 * 1024,
            MaxPatchBytes = 1024 * 1024,
            MaxFileWriteBytes = 1024 * 1024,
            MaxCommandOutputBytes = 256 * 1024,
            MaxChangedFiles = 32,
            MaxToolCalls = 16,
            MaxAttemptDurationSeconds = 60,
            MaxOutputTokens = 2048
        };

    /// <summary>
    ///     What a Tool node executor would synthesize: the real project binding, the node-run id in both isolation
    ///     slots, and the objective fields carrying the node rather than a Development task.
    /// </summary>
    private static DevelopmentExecutionSnapshot NodeRunSnapshot(Guid projectId, Guid nodeRunId, string identity) =>
        new(projectId,
            nodeRunId,
            nodeRunId,
            Guid.NewGuid(),
            identity,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "validate",
            "Run the project's validation commands.",
            "[]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 1,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,
            Encoding.UTF8.GetString(GenericProfile.ToCanonicalUtf8()));

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.email", "dev-workflow-spike@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.name", "Dev Workflow Spike").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, ".env"), "AWS_SECRET_ACCESS_KEY=devworkflowsentinel\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "-A", "--", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);
        return repository;
    }
}
