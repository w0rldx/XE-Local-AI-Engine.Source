namespace XE_Local_AI_Engine.Tests.AgentHome;

using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker I-pre gateway-adapter coverage: the <see cref="AgentHomeToolGateway" /> renders a successful run into a
///     compact model-facing string and maps the two pre-provider policy rejections (unknown folder id, disallowed
///     runtime profile) onto a clear rejection, while letting cancellation propagate. The service is faked.
/// </summary>
public sealed class AgentHomeToolGatewayTests
{
    private static readonly AgentHomeRunToolRequest ValidRequest = new()
    {
        Goal = "analyze the project",
        SelectedFolderIds = ["3f2504e0-4f89-41d3-9a0c-0305e82c3301"],
        AllowedActions = ["read_workspace"]
    };

    [Test]
    public async Task ExecuteAsync_WhenRunSucceeds_RendersCompactResult()
    {
        var gateway = new AgentHomeToolGateway(
            new StubAgentHomeService(new AgentHomeRunResult
            {
                RunId = "run-123",
                Completed = true,
                ExitCode = 0,
                LogPath = "/tmp/agent-home/runs/run-123/logs"
            }));

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "run-123");
        AssertEx.Contains(result, "completed", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "runs/run-123");
        AssertEx.False(result.Contains("/tmp/agent-home", StringComparison.Ordinal), "the model must not see the absolute worker-host path");
    }

    [Test]
    public async Task ExecuteAsync_WhenFolderIdUnknown_RendersRejection()
    {
        var gateway = new AgentHomeToolGateway(
            StubAgentHomeService.ThatThrows(new SelectedFolderValidationException("Unknown selected folder id.")));

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "Unknown selected folder id.");
    }

    [Test]
    public async Task ExecuteAsync_WhenRuntimeProfileRejected_RendersRejection()
    {
        var gateway = new AgentHomeToolGateway(
            StubAgentHomeService.ThatThrows(new AgentHomeRequestRejectedException("runtime profile 'x' is not enabled on this node.")));

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "rejected", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteAsync_WhenCancelled_Throws()
    {
        var gateway = new AgentHomeToolGateway(new StubAgentHomeService(new AgentHomeRunResult
        {
            RunId = "run-1",
            Completed = true,
            ExitCode = 0,
            LogPath = "/tmp/logs"
        }));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            gateway.ExecuteAsync(ValidRequest, cancellation.Token));
    }

    private sealed class StubAgentHomeService : IAgentHomeService
    {
        private readonly Exception? _prepareError;
        private readonly AgentHomeRunResult? _runResult;

        public StubAgentHomeService(AgentHomeRunResult runResult)
        {
            _runResult = runResult;
        }

        private StubAgentHomeService(Exception prepareError)
        {
            _prepareError = prepareError;
        }

        public static StubAgentHomeService ThatThrows(Exception prepareError)
        {
            return new StubAgentHomeService(prepareError);
        }

        public Task<AgentHomePrepareResult> PrepareAsync(AgentHomePrepareRequest request, CancellationToken cancellationToken = default)
        {
            if (_prepareError is not null)
            {
                return Task.FromException<AgentHomePrepareResult>(_prepareError);
            }

            return Task.FromResult(BuildPrepareResult());
        }

        public Task<AgentHomeRunResult> RunAsync(AgentHomeRunRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_runResult!);
        }

        private static AgentHomePrepareResult BuildPrepareResult()
        {
            var attachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "fake",
                RuntimeProfile = "dotnet-agent-home",
                ManifestVersion = AgentHomeManifest.CurrentVersion
            };

            var manifest = new AgentHomeManifest
            {
                Version = AgentHomeManifest.CurrentVersion,
                Status = AgentHomeStatus.Ready,
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = "fake",
                RuntimeProfile = "dotnet-agent-home",
                CreatedAt = default,
                UpdatedAt = default
            };

            return new AgentHomePrepareResult
            {
                Layout = new AgentHomeLayout { RootPath = "/tmp/agent-home", Manifest = manifest },
                Handle = new SandboxHandle
                {
                    ProviderName = "fake",
                    SandboxId = "fake-sandbox-1",
                    AttachKey = attachKey,
                    CreatedAt = default,
                    ManifestVersion = AgentHomeManifest.CurrentVersion
                },
                ResolvedFolders = [],
                RuntimeProfile = "dotnet-agent-home"
            };
        }
    }
}
