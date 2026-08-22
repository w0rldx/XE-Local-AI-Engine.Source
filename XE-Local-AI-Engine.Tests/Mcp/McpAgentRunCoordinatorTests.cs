namespace XE_Local_AI_Engine.Tests.Mcp;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Mcp.Runs;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class McpAgentRunCoordinatorTests
{
    private const string LegacyDelegateRequestFingerprint = "22D00EDA7B70A9C1F32B01A3CFAAE6BEFB3DC9137D8C097F70C9469AC27886C8";

    [Test]
    public void RequestFingerprint_WhenDelegateRequestMatchesPreMigrationCanonical_RemainsV1Compatible()
    {
        using var harness = new Harness();
        var request = new McpAgentRunStartRequest(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "inspect",
            new McpExecutionBindingRequest
            {
                ModelId = "model"
            });

        AssertEx.Equal(LegacyDelegateRequestFingerprint, Convert.ToHexString(harness.ComputeFingerprint(request)));
    }

    [Test]
    public void RequestFingerprint_WhenAuthorityDiffers_DoesNotAliasRequestIdentity()
    {
        using var harness = new Harness();
        var requestId = Guid.NewGuid();
        var delegateRequest = new McpAgentRunStartRequest(requestId,
            "inspect",
            new McpExecutionBindingRequest
            {
                ModelId = "model"
            });
        var agenticRequest = delegateRequest with
        {
            Binding = delegateRequest.Binding with
            {
                InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_abc123")
            }
        };
        var otherAgenticRequest = agenticRequest with
        {
            Binding = agenticRequest.Binding with
            {
                InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_def456")
            }
        };

        AssertEx.False(harness.ComputeFingerprint(delegateRequest).SequenceEqual(harness.ComputeFingerprint(agenticRequest)));
        AssertEx.False(harness.ComputeFingerprint(agenticRequest).SequenceEqual(harness.ComputeFingerprint(otherAgenticRequest)));
    }

    [Test]
    public async Task StartAsync_WhenMigratedQueuedDelegateRetries_ReusesLegacyIdentityWithoutResolution()
    {
        using var harness = new Harness();
        var request = new McpAgentRunStartRequest(Guid.Parse("11111111-2222-3333-4444-555555555555"),
            "inspect",
            new McpExecutionBindingRequest
            {
                ModelId = "model"
            });
        harness.Store.GetAsync(request.RequestId, Arg.Any<CancellationToken>())
               .Returns(CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None) with
               {
                   RequestId = request.RequestId,
                   RequestFingerprint = Convert.FromHexString(LegacyDelegateRequestFingerprint),
                   IsAgenticAutoApprove = false,
                   RequestingKeyPrefix = null
               });

        var result = await harness.Coordinator.StartAsync(request, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Existing, result.Kind);
        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await harness.Store.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default);
    }

    [Test]
    public async Task StartAsync_WithExistingWorkspaceRun_ReturnsExistingBeforeWorkspaceAuthorization()
    {
        using var harness = new Harness();
        var workspaceId = Guid.NewGuid();
        var request = new McpAgentRunStartRequest(Guid.NewGuid(),
            "inspect the repository",
            new McpExecutionBindingRequest
            {
                ModelId = "local-model"
            },
            workspaceId);
        var existing = CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None) with
        {
            RequestId = request.RequestId,
            RequestFingerprint = harness.ComputeFingerprint(request),
            WorkspaceId = workspaceId
        };
        harness.Store.GetAsync(request.RequestId, Arg.Any<CancellationToken>()).Returns(existing);

        var result = await harness.Coordinator.StartAsync(request, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Existing, result.Kind);
        await harness.Store.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default);
        await harness.Resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await harness.WorkspaceResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
    }

    [Test]
    public async Task StartAsync_WithExactCoderAndNoWorkspace_RejectsBeforeAdmission()
    {
        using var harness = new Harness();
        harness.Resolver.ResolveAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<CancellationToken>())
               .Returns(McpExecutionBindingResolution.Success(ExactCoderBinding()));
        var request = new McpAgentRunStartRequest(Guid.NewGuid(),
            "inspect the repository",
            new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            });

        var result = await harness.Coordinator.StartAsync(request, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Rejected, result.Kind);
        AssertEx.Equal(McpAgentRunFailureCodes.WorkspaceNotAuthorized, result.FailureCode!);
        AssertEx.Equal("Cannot start: the selected workspace is not authorized.", result.DisplayMessage);
        await harness.WorkspaceResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await harness.Store.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default);
    }

    [Test]
    public async Task StartAsync_WithUnknownOrRevokedWorkspace_ReturnsSamePathFreeRejection()
    {
        using var harness = new Harness();
        var binding = ExactCoderBinding();
        harness.Resolver.ResolveAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<CancellationToken>())
               .Returns(McpExecutionBindingResolution.Success(binding));
        harness.WorkspaceResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns<Task<ResolvedSelectedFolder>>(_ => throw new SelectedFolderValidationException("not active"));

        var unknown = await harness.Coordinator.StartAsync(WorkspaceRequest(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var revoked = await harness.Coordinator.StartAsync(WorkspaceRequest(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Rejected, unknown.Kind);
        AssertEx.Equal(McpAgentRunFailureCodes.WorkspaceNotAuthorized, unknown.FailureCode!);
        AssertEx.Equal(unknown.DisplayMessage, revoked.DisplayMessage);
        AssertEx.False(unknown.DisplayMessage.Contains('/', StringComparison.Ordinal), "workspace rejection must not disclose a path.");
        await harness.Store.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default);
    }

    [Test]
    public async Task StartAsync_WithWorkspaceAndNonCoderBinding_RejectsBeforeWorkspaceResolution()
    {
        using var harness = new Harness();
        harness.Resolver.ResolveAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<CancellationToken>())
               .Returns(McpExecutionBindingResolution.Success(ExactCoderBinding() with
               {
                   AllowedTools = []
               }));

        var result = await harness.Coordinator.StartAsync(WorkspaceRequest(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunFailureCodes.WorkspaceNotAuthorized, result.FailureCode!);
        await harness.WorkspaceResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default);
        await harness.Store.DidNotReceiveWithAnyArgs().AdmitAsync(default!, default);
    }

    [Test]
    public async Task StartAsync_WithActiveWorkspaceAndExactCoderBinding_PersistsOnlyOpaqueWorkspaceId()
    {
        using var harness = new Harness();
        var workspaceId = Guid.NewGuid();
        var binding = ExactCoderBinding();
        harness.Resolver.ResolveAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<CancellationToken>())
               .Returns(McpExecutionBindingResolution.Success(binding));
        harness.WorkspaceResolver.ResolveAsync(workspaceId.ToString("D"), Arg.Any<CancellationToken>())
               .Returns(new ResolvedSelectedFolder(workspaceId, "repo", "/private/not-persisted", SelectedFolderMode.ReadOnlyMount));
        McpAgentRunAdmissionRequest? captured = null;
        harness.Store.AdmitAsync(Arg.Any<McpAgentRunAdmissionRequest>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   captured = callInfo.Arg<McpAgentRunAdmissionRequest>();
                   return new McpAgentRunAdmissionResult(McpAgentRunAdmissionKind.Accepted,
                       CreateRun(McpAgentRunStatus.Queued, version: 0, claimToken: null, McpAgentRunStopReason.None) with
                       {
                           RequestId = captured.RequestId,
                           WorkspaceId = captured.WorkspaceId,
                           ClaimedAtUtc = null,
                           PayloadExpiresAtUtc = null
                       });
               });

        var result = await harness.Coordinator.StartAsync(WorkspaceRequest(workspaceId), CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Accepted, result.Kind);
        AssertEx.Equal(workspaceId, captured!.WorkspaceId!.Value);
        AssertEx.False(captured.Task.Contains("/private/not-persisted", StringComparison.Ordinal),
            "the admission payload must never contain the resolved host path.");
    }

    [Test]
    public async Task StartAsync_AcceptedQueuedRun_HasNoAcceptanceBasedPayloadExpiry()
    {
        using var harness = new Harness();
        var requestId = Guid.NewGuid();
        var bindingRequest = new McpExecutionBindingRequest
        {
            ModelId = "local-model"
        };
        var binding = new McpExecutionBinding(Convert.ToHexString(SHA256.HashData("binding"u8)),
            "local-model",
            "read only",
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            AllowedTools: [],
            ReasoningEffort: null,
            SupportsThinking: false);
        harness.Resolver.ResolveAsync(bindingRequest, Arg.Any<CancellationToken>())
               .Returns(McpExecutionBindingResolution.Success(binding));
        McpAgentRunAdmissionRequest? admission = null;
        harness.Store.AdmitAsync(Arg.Any<McpAgentRunAdmissionRequest>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   admission = callInfo.Arg<McpAgentRunAdmissionRequest>();
                   var queued = CreateRun(McpAgentRunStatus.Queued,
                           version: 0,
                           claimToken: null,
                           McpAgentRunStopReason.None) with
                       {
                           RequestId = requestId,
                           ClaimedAtUtc = null,
                           PayloadExpiresAtUtc = null
                       };
                   return new McpAgentRunAdmissionResult(McpAgentRunAdmissionKind.Accepted, queued);
               });
        harness.Store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>())
               .Returns(EmptySnapshot());

        var result = await harness.Coordinator.StartAsync(new McpAgentRunStartRequest(requestId,
                "inspect the repository",
                bindingRequest),
            CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunStartKind.Accepted, result.Kind);
        AssertEx.NotNull(admission);
        AssertEx.Null(result.Run!.PayloadExpiresAtUtc);
        await harness.Store.Received(1).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelAsync_WhenCalledRepeatedly_ReturnsRequestedThenAlreadyRequestedAndSignalsRegisteredExecution()
    {
        using var harness = new Harness();
        var claimToken = Guid.NewGuid();
        var running = CreateRun(McpAgentRunStatus.Running, version: 1, claimToken, McpAgentRunStopReason.None);
        var stopped = running with
        {
            Version = 2,
            StopReason = McpAgentRunStopReason.UserCancellation,
            StopRequestedAtUtc = 20
        };
        var reads = 0;
        var stops = 0;
        harness.Store.GetAsync(running.RequestId, Arg.Any<CancellationToken>())
               .Returns(_ => Interlocked.Increment(ref reads) == 1 ? running : stopped);
        harness.Store.RequestStopAsync(running.RequestId,
                   Arg.Any<long>(),
                   McpAgentRunStopReason.UserCancellation,
                   Arg.Any<long>(),
                   Arg.Any<CancellationToken>())
               .Returns(_ => Interlocked.Increment(ref stops) == 1
                   ? new McpAgentRunStopResult(McpAgentRunStopKind.Requested, stopped)
                   : new McpAgentRunStopResult(McpAgentRunStopKind.AlreadyRequested, stopped));
        AssertEx.Equal(McpAgentRunRegistrationKind.Registered,
            harness.Cancellations.TryRegister(running.RequestId, claimToken, running.Version, out var executionToken));

        var first = await harness.Coordinator.CancelAsync(running.RequestId, CancellationToken.None).ConfigureAwait(false);
        var second = await harness.Coordinator.CancelAsync(running.RequestId, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunCancelKind.Requested, first.Kind);
        AssertEx.Equal(McpAgentRunCancelKind.AlreadyRequested, second.Kind);
        AssertEx.True(executionToken.IsCancellationRequested,
            "A repeated cancellation must still signal a live execution whose durable marker is already present.");
        await harness.Store.Received(1).GetLedgerSnapshotAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelAsync_WhenCompletionAlreadyWon_ReturnsAlreadyTerminalWithoutSignallingOldClaim()
    {
        using var harness = new Harness();
        var claimToken = Guid.NewGuid();
        var completed = CreateRun(McpAgentRunStatus.Succeeded, version: 2, claimToken, McpAgentRunStopReason.None) with
        {
            Result = "completed first",
            CompletedAtUtc = 30
        };
        harness.Store.GetAsync(completed.RequestId, Arg.Any<CancellationToken>()).Returns(completed);
        harness.Store.RequestStopAsync(completed.RequestId,
                   completed.Version,
                   McpAgentRunStopReason.UserCancellation,
                   Arg.Any<long>(),
                   Arg.Any<CancellationToken>())
               .Returns(new McpAgentRunStopResult(McpAgentRunStopKind.AlreadyTerminal, completed));
        AssertEx.Equal(McpAgentRunRegistrationKind.Registered,
            harness.Cancellations.TryRegister(completed.RequestId, claimToken, version: 1, out var executionToken));

        var result = await harness.Coordinator.CancelAsync(completed.RequestId, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(McpAgentRunCancelKind.AlreadyTerminal, result.Kind);
        AssertEx.False(executionToken.IsCancellationRequested,
            "A terminal completion is immutable and must not be retroactively cancelled.");
    }

    private static McpAgentRunRecord CreateRun(McpAgentRunStatus status,
        long version,
        Guid? claimToken,
        McpAgentRunStopReason stopReason) =>
        new(Guid.Parse("74134b4f-a62c-4398-a01a-17d939600335"),
            SHA256.HashData("request"u8),
            status,
            version,
            claimToken,
            stopReason,
            StopRequestedAtUtc: null,
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            ModelId: "local-model",
            ModelOverrideId: null,
            WorkspaceId: null,
            BindingFingerprint: SHA256.HashData("binding"u8),
            Task: "task",
            Instructions: "read only",
            Result: null,
            DisplayMessage: null,
            FailureCode: null,
            CreatedAtUtc: 1,
            ClaimedAtUtc: 2,
            CompletedAtUtc: null,
            PayloadExpiresAtUtc: 86_400_001,
            CompactedAtUtc: null,
            PayloadExpired: false);

    private static McpAgentRunLedgerSnapshot EmptySnapshot() =>
        new(QueueDepth: 0,
            RunningCount: 0,
            new McpAgentRunLedgerCounters(AccountingVersion: 1,
                NonterminalRunCount: 0,
                QueuedRunCount: 0,
                RunningRunCount: 0,
                IdentityCount: 0,
                ActivePayloadBytes: 0,
                TombstoneLogicalBytes: 0,
                UpdatedAtUtc: 0));

    private static McpAgentRunStartRequest WorkspaceRequest(Guid workspaceId) =>
        new(Guid.NewGuid(),
            "inspect the repository",
            new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            workspaceId);

    private static McpExecutionBinding ExactCoderBinding() =>
        new(Convert.ToHexString(SHA256.HashData("coder-binding"u8)),
            "local-model",
            "read only",
            Guid.NewGuid(),
            AgentDefinitionVersion: 1,
            AllowedTools:
            [
                Tool("list_files"),
                Tool("read_file"),
                Tool("search_text")
            ],
            ReasoningEffort: null,
            SupportsThinking: false);

    private static AllowedToolDto Tool(string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            Category = ToolCategory.ReadLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = false
        };

    private sealed class Harness : IDisposable
    {
        private readonly FixedNodeSqliteKeyHolder _keyHolder = new();
        private readonly McpAgentRunMetrics _metrics = new();
        private readonly McpAgentRunPayloadProtector _protector;

        public Harness()
        {
            _protector = new McpAgentRunPayloadProtector(_keyHolder, new AesGcmNodeAeadCipher());
            Cancellations = new McpAgentRunCancellationRegistry();
            Resolver = Substitute.For<IMcpExecutionBindingResolver>();
            WorkspaceResolver = Substitute.For<ISelectedFolderResolver>();
            Store.GetLedgerSnapshotAsync(Arg.Any<CancellationToken>()).Returns(EmptySnapshot());
            Fingerprint = new McpAgentRunRequestFingerprint(_protector);
            Coordinator = new McpAgentRunCoordinator(Store,
                Resolver,
                Fingerprint,
                Cancellations,
                _metrics,
                WorkspaceResolver,
                Options.Create(new McpAgentRunOptions()),
                TimeProvider.System,
                NullLogger<McpAgentRunCoordinator>.Instance);
        }

        public McpAgentRunCancellationRegistry Cancellations { get; }

        public McpAgentRunCoordinator Coordinator { get; }

        public IMcpExecutionBindingResolver Resolver { get; }

        public McpAgentRunRequestFingerprint Fingerprint { get; }

        public IMcpAgentRunStore Store { get; } = Substitute.For<IMcpAgentRunStore>();

        public ISelectedFolderResolver WorkspaceResolver { get; }

        public byte[] ComputeFingerprint(McpAgentRunStartRequest request) =>
            Fingerprint.Compute(request);

        public void Dispose()
        {
            _metrics.Dispose();
            _protector.Dispose();
            _keyHolder.Dispose();
        }
    }

    private sealed class FixedNodeSqliteKeyHolder : INodeSqliteKeyHolder
    {
        private byte[]? _key = SHA256.HashData(Encoding.UTF8.GetBytes("mcp-coordinator-test-key"));

        public ReadOnlyMemory<byte> Key => _key ?? throw new ObjectDisposedException(nameof(FixedNodeSqliteKeyHolder));

        public void Dispose()
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
        }
    }
}
