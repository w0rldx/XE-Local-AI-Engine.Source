namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Gateway-adapter coverage: the <see cref="AgentHomeToolGateway" /> renders a successful run into a
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

    private static readonly IOptions<AgentHomeOptions> GatewayOptions =
        Microsoft.Extensions.Options.Options.Create(new AgentHomeOptions { CommandTimeoutSeconds = 300 });

    [Test]
    public async Task ExecuteAsync_WhenRunSucceeds_RendersCompactResult()
    {
        var gateway = new AgentHomeToolGateway(
            new StubAgentHomeService(new AgentHomeRunResult
            {
                RunId = "run-123",
                Completed = true,
                ExitCode = 0,
                LogPath = "/tmp/agent-home/runs/run-123/logs",
                Patch = new AgentHomePatchExport
                {
                    ChangedFileCount = 2,
                    Blocked = false,
                    PatchBytes = 1024,
                    PatchRelativePath = "runs/run-123/patches/changes.patch",
                    ChangedFilesRelativePath = "runs/run-123/patches/changed-files.json"
                }
            }),
            GatewayOptions);

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "run-123");
        AssertEx.Contains(result, "completed", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "runs/run-123");
        AssertEx.Contains(result, "2 file(s) changed");
        AssertEx.Contains(result, "runs/run-123/patches/changes.patch");
        AssertEx.False(result.Contains("/tmp/agent-home", StringComparison.Ordinal), "the model must not see the absolute worker-host path");
    }

    [Test]
    public async Task ExecuteAsync_WhenPatchBlocked_RendersBudgetNoticeWithoutPatchPath()
    {
        var gateway = new AgentHomeToolGateway(
            new StubAgentHomeService(new AgentHomeRunResult
            {
                RunId = "run-789",
                Completed = true,
                ExitCode = 0,
                LogPath = "/tmp/agent-home/runs/run-789/logs",
                Patch = new AgentHomePatchExport
                {
                    ChangedFileCount = 5,
                    Blocked = true,
                    PatchBytes = 99999999,
                    PatchRelativePath = null,
                    ChangedFilesRelativePath = "runs/run-789/patches/changed-files.json"
                }
            }),
            GatewayOptions);

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "5 file(s) changed");
        AssertEx.Contains(result, "size budget", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "runs/run-789/patches/changed-files.json");
        AssertEx.False(result.Contains("changes.patch", StringComparison.Ordinal), "a blocked patch is not written, so its path must not render");
    }

    [Test]
    public async Task ExecuteAsync_WhenPatchExportFailed_RendersFailureNotice()
    {
        var gateway = new AgentHomeToolGateway(
            new StubAgentHomeService(new AgentHomeRunResult
            {
                RunId = "run-f",
                Completed = true,
                ExitCode = 0,
                LogPath = "/tmp/agent-home/runs/run-f/logs",
                Patch = new AgentHomePatchExport
                {
                    ChangedFileCount = 0,
                    Blocked = false,
                    Failed = true,
                    PatchBytes = 0,
                    PatchRelativePath = null,
                    ChangedFilesRelativePath = null
                }
            }),
            GatewayOptions);

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "export failed", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ExecuteAsync_WhenFolderIdUnknown_RendersRejection()
    {
        var gateway = new AgentHomeToolGateway(
            StubAgentHomeService.ThatThrows(new SelectedFolderValidationException("Unknown selected folder id.")),
            GatewayOptions);

        var result = await gateway.ExecuteAsync(ValidRequest);

        AssertEx.Contains(result, "rejected", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(result, "Unknown selected folder id.");
    }

    [Test]
    public async Task ExecuteAsync_WhenRuntimeProfileRejected_RendersRejection()
    {
        var gateway = new AgentHomeToolGateway(
            StubAgentHomeService.ThatThrows(new AgentHomeRequestRejectedException("runtime profile 'x' is not enabled on this node.")),
            GatewayOptions);

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
            LogPath = "/tmp/logs",
            Patch = new AgentHomePatchExport
            {
                ChangedFileCount = 0,
                Blocked = false,
                PatchBytes = 0,
                PatchRelativePath = null,
                ChangedFilesRelativePath = null
            }
        }), GatewayOptions);
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

        public async Task<AgentHomeRunResult> RunLifecycleAsync(AgentHomeRunLifecycleRequest request, CancellationToken cancellationToken = default)
        {
            // Mirror the real lifecycle: a Prepare error (policy rejection) surfaces here, otherwise the stub run result
            // is returned. Routing through PrepareAsync keeps the ThatThrows(...) cases exercising the same path.
            _ = await PrepareAsync(
                new AgentHomePrepareRequest
                {
                    SelectedFolderIds = request.SelectedFolderIds,
                    RuntimeProfile = request.RuntimeProfile
                },
                cancellationToken).ConfigureAwait(false);

            return _runResult!;
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
                FolderSnapshots = [],
                RuntimeProfile = "dotnet-agent-home"
            };
        }
    }
}
