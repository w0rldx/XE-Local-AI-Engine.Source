namespace XE_Local_AI_Engine.Tests.Connection;

using System.Net.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Connection.Implementation;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Tests.Fixtures;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Each test here hosts a real TLS Kestrel worker-node fixture and drives genuine SignalR-over-WebSockets transport.
///     The reconnect cases fire a transport-level drop and wait on automatic reconnect, whose exponential backoff starts
///     at one second and doubles each attempt, so a handshake that is CPU-starved past its early retry windows can blow
///     the thirty-second wait budget. Under the full module's parallel load that starvation is real and intermittent, so
///     this heavy, timing-sensitive integration suite runs in isolation — the same keyless-NotInParallel idiom the CUDA
///     build and stream-idle-watchdog suites use. Given the CPU to complete a reconnect on its first retry, the waits are
///     never the bottleneck.
/// </summary>
[NotInParallel]
public sealed class WorkerHubConnectionSignalRIntegrationTests
{
    [Test]
    public async Task SendCapabilitiesAsync_WhenWorkerReportsCapabilities_SendsServerHubContractShape()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var tokenStore = MockTokenStore.Paired("test-access-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        var sender = new MockHubMessageSender();
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        deadLetterStore.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));

        var deadLetterFlushService = new DeadLetterFlushService(deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = new WorkerHubConnection(tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = fixture.HubBaseUri.ToString(),
                HubPath = fixture.HubPath
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => capabilityReporter),
            deadLetterFlushService,
            nodeKeyRegistry,
            NullLogger<WorkerHubConnection>.Instance,
            CreateFixtureHttpOptionsConfigurator(fixture));

        await connection.ConnectAsync();

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
        await connection.SendCapabilitiesAsync(new ClientCapabilities
        {
            SchemaVersion = 2,
            RamMb = 32000,
            VramMb = 16000,
            CudaAvailable = true,
            GpuName = "RTX",
            CpuClass = "desktop",
            SystemScoreClass = "High",
            OllamaReachable = true,
            OllamaVersion = "0.0.0-test",
            ManagementMode = "unmanaged",
            LastCapabilityReportAt = expiresAt.AddMinutes(-1),
            Diagnostics = ["test-diagnostic"],
            InstalledModels = ["qwen3.5:0.8b"],
            InstalledModelMetadata =
            [
                new ClientModelMetadata
                {
                    Name = "qwen3.5:0.8b",
                    Digest = "sha256:qwen-test",
                    MaxContextTokens = 32768
                }
            ],
            SupportedCapabilities = ["text"],
            ActiveModel = "qwen3.5:0.8b",
            ActiveModelExpiresAt = expiresAt
        });

        var payload = await fixture.WaitForCapabilitiesAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(expected: 32000, payload.HardwareInfo.RamMb);
        AssertEx.Equal(expected: 16000, payload.HardwareInfo.VramMb);
        AssertEx.True(payload.HardwareInfo.CudaAvailable);
        AssertEx.Equal("RTX", payload.HardwareInfo.GpuName);
        AssertEx.Equal("desktop", payload.HardwareInfo.CpuClass);
        AssertEx.Equal(expected: 2, payload.Capabilities.SchemaVersion);
        AssertEx.Equal("High", payload.Capabilities.SystemScoreClass);
        AssertEx.True(payload.Capabilities.OllamaReachable == true);
        AssertEx.Equal("0.0.0-test", payload.Capabilities.OllamaVersion);
        AssertEx.Equal("unmanaged", payload.Capabilities.ManagementMode);
        AssertEx.Equal(expiresAt.AddMinutes(-1), payload.Capabilities.LastCapabilityReportAt);
        AssertEx.Contains(payload.Capabilities.Diagnostics, "test-diagnostic");
        AssertEx.Contains(payload.Capabilities.InstalledModels, "qwen3.5:0.8b");
        AssertEx.ContainsSingle(payload.Capabilities.InstalledModelMetadata,
            model => model.Name == "qwen3.5:0.8b"
                     && model.Digest == "sha256:qwen-test"
                     && model.MaxContextTokens == 32768);
        AssertEx.Contains(payload.Capabilities.SupportedCapabilities, "text");
        AssertEx.Equal("qwen3.5:0.8b", payload.Capabilities.ActiveModel);
        AssertEx.Equal(expiresAt, payload.Capabilities.ActiveModelExpiresAt);
    }

    [Test]
    public async Task EncryptedRuntimePackageRoundTrip_WhenWorkerReceivesInvocationAndSendsChunkAndCompleted_PreservesPayloads()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var tokenStore = MockTokenStore.Paired("test-access-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        var sender = new MockHubMessageSender();
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        deadLetterStore.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));

        var deadLetterFlushService = new DeadLetterFlushService(deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = new WorkerHubConnection(tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = fixture.HubBaseUri.ToString(),
                HubPath = fixture.HubPath
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => capabilityReporter),
            deadLetterFlushService,
            nodeKeyRegistry,
            NullLogger<WorkerHubConnection>.Instance,
            CreateFixtureHttpOptionsConfigurator(fixture));

        var invocationAssigned = new TaskCompletionSource<EncryptedRuntimePackageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.InvocationAssignedReceived += (_, args) => invocationAssigned.TrySetResult(args.EncryptedRuntimePackage);

        await connection.ConnectAsync();

        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        const int epochVersion = 7;

        var conversationContext = new List<EncryptedConversationMessageDto>
        {
            new()
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Role = MessageRole.User,
                SortOrder = 10,
                EpochVersion = epochVersion,
                Aad = $"message|{conversationId:D}|11111111-1111-1111-1111-111111111111|{epochVersion}",
                NodeWrappedEpochKey = new byte[]
                {
                    1,
                    2,
                    3
                },
                ClientEphemeralPublicKey = new byte[]
                {
                    4,
                    5,
                    6
                },
                Ciphertext = new byte[]
                {
                    7,
                    8,
                    9
                },
                ContentIv = new byte[]
                {
                    10,
                    11,
                    12
                }
            }
        };

        var runtimePackage = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ClientNodeId = Guid.NewGuid(),
            MessageId = messageId,
            EpochVersion = epochVersion,
            AgentDefinitionVersion = 7,
            ResolvedSystemPrompt = "You are a helpful local AI assistant.",
            AllowedTools =
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\"}"
                }
            ],
            ModelProfile = "balanced-local-v1",
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            ConfigHash = "04c79b399e8dd0a4eba7e2b50c43931aa92b7c50ed73db6d1989c209f3c1cf33",
            ConversationContext = conversationContext,
            ConversationContextHash = RuntimePackageHistoryHash.Compute(conversationContext),
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3,
                4
            },
            ClientEphemeralPublicKey = new byte[]
            {
                5,
                6,
                7,
                8
            },
            Ciphertext = new byte[]
            {
                9,
                10,
                11,
                12
            },
            ContentIv = new byte[]
            {
                13,
                14,
                15,
                16
            },
            Aad = $"message|{conversationId:D}|{messageId:D}|{epochVersion}"
        };

        await fixture.SendInvocationAssignedAsync(runtimePackage);

        var receivedPackage = await invocationAssigned.Task.WaitAsync(TestBudgets.Contended);
        AssertEncryptedRuntimePackageEqual(runtimePackage, receivedPackage);

        var chunkPayload = new EncryptedChunkEnvelopeV1
        {
            ConversationId = runtimePackage.ConversationId,
            MessageId = runtimePackage.MessageId,
            EpochVersion = runtimePackage.EpochVersion,
            ChunkIv = new byte[]
            {
                21,
                22,
                23,
                24
            },
            ChunkCiphertext = new byte[]
            {
                25,
                26,
                27,
                28
            },
            Sequence = 1
        };

        var completedPayload = new EncryptedCompletedEnvelopeV1
        {
            ConversationId = runtimePackage.ConversationId,
            MessageId = runtimePackage.MessageId,
            EpochVersion = runtimePackage.EpochVersion,
            FinalIv = new byte[]
            {
                31,
                32,
                33,
                34
            },
            FinalCiphertext = new byte[]
            {
                35,
                36,
                37,
                38
            },
            TotalSequence = 2,
            TokenCounts = new Dictionary<string, long>
            {
                ["input"] = 11,
                ["output"] = 7
            }
        };

        await connection.SendEncryptedChunkAsync(chunkPayload);
        await connection.SendEncryptedCompletedAsync(completedPayload);

        var receivedChunk = await fixture.WaitForFirstChunkAsync(TimeSpan.FromSeconds(5));
        var receivedCompleted = await fixture.WaitForCompletedAsync(TimeSpan.FromSeconds(5));

        AssertEncryptedChunkEqual(chunkPayload, receivedChunk);
        AssertEncryptedCompletedEqual(completedPayload, receivedCompleted);
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EncryptedRuntimePackageRoundTrip_WhenWorkerReceivesMultiMessageHistory_PreservesConversationContext()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var tokenStore = MockTokenStore.Paired("test-access-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(30));
        var sender = new MockHubMessageSender();
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        deadLetterStore.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));

        var deadLetterFlushService = new DeadLetterFlushService(deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = new WorkerHubConnection(tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = fixture.HubBaseUri.ToString(),
                HubPath = fixture.HubPath
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => capabilityReporter),
            deadLetterFlushService,
            nodeKeyRegistry,
            NullLogger<WorkerHubConnection>.Instance,
            CreateFixtureHttpOptionsConfigurator(fixture));

        var invocationAssigned = new TaskCompletionSource<EncryptedRuntimePackageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.InvocationAssignedReceived += (_, args) => invocationAssigned.TrySetResult(args.EncryptedRuntimePackage);

        await connection.ConnectAsync();

        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var historyMessageOneId = Guid.NewGuid();
        var historyMessageTwoId = Guid.NewGuid();
        const int epochVersion = 7;
        var conversationContext = new List<EncryptedConversationMessageDto>
        {
            new()
            {
                Id = historyMessageOneId,
                Role = MessageRole.User,
                SortOrder = 10,
                EpochVersion = epochVersion,
                Aad = $"message|{conversationId:D}|{historyMessageOneId:D}|{epochVersion}",
                NodeWrappedEpochKey = new byte[]
                {
                    1,
                    2,
                    3
                },
                ClientEphemeralPublicKey = new byte[]
                {
                    4,
                    5,
                    6
                },
                Ciphertext = new byte[]
                {
                    7,
                    8,
                    9
                },
                ContentIv = new byte[]
                {
                    10,
                    11,
                    12
                }
            },
            new()
            {
                Id = historyMessageTwoId,
                Role = MessageRole.Assistant,
                SortOrder = 20,
                EpochVersion = epochVersion,
                Aad = $"message|{conversationId:D}|{historyMessageTwoId:D}|{epochVersion}",
                NodeWrappedEpochKey = new byte[]
                {
                    11,
                    12,
                    13
                },
                ClientEphemeralPublicKey = new byte[]
                {
                    14,
                    15,
                    16
                },
                Ciphertext = new byte[]
                {
                    17,
                    18,
                    19
                },
                ContentIv = new byte[]
                {
                    20,
                    21,
                    22
                }
            }
        };

        var runtimePackage = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ClientNodeId = Guid.NewGuid(),
            MessageId = messageId,
            EpochVersion = epochVersion,
            AgentDefinitionVersion = 7,
            ResolvedSystemPrompt = "You are a helpful local AI assistant.",
            AllowedTools =
            [
                new MixedEnvelopeAllowedToolDto
                {
                    Name = "open_url",
                    Description = "Open a URL in the worker browser",
                    Schema = "{\"type\":\"object\"}"
                }
            ],
            ModelProfile = "balanced-local-v1",
            Timeouts = new TimeoutSettings
            {
                InvocationTimeoutSeconds = 300,
                ToolCallTimeoutSeconds = 60,
                StreamIdleTimeoutSeconds = 30
            },
            ConfigHash = "04c79b399e8dd0a4eba7e2b50c43931aa92b7c50ed73db6d1989c209f3c1cf33",
            ConversationContext = conversationContext,
            ConversationContextHash = RuntimePackageHistoryHash.Compute(conversationContext),
            NodeWrappedEpochKey = new byte[]
            {
                1,
                2,
                3,
                4
            },
            ClientEphemeralPublicKey = new byte[]
            {
                5,
                6,
                7,
                8
            },
            Ciphertext = new byte[]
            {
                9,
                10,
                11,
                12
            },
            ContentIv = new byte[]
            {
                13,
                14,
                15,
                16
            },
            Aad = $"message|{conversationId:D}|{messageId:D}|{epochVersion}"
        };

        await fixture.SendInvocationAssignedAsync(runtimePackage);

        var receivedPackage = await invocationAssigned.Task.WaitAsync(TestBudgets.Contended);
        AssertEncryptedRuntimePackageEqual(runtimePackage, receivedPackage);
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendHeartbeatAsync_WhenConnected_DeliversHeartbeatPayload()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var heartbeatTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var timeProvider = new FixedTimeProvider(heartbeatTimestamp);

        var clientNodeId = Guid.NewGuid();
        // The token must stay fresh relative to BOTH the injected clock (token-refresh check) and the
        // real wall clock (MockTokenStore.IsTokenExpired), so anchor expiry beyond both far into the future.
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, heartbeatTimestamp.AddMinutes(30));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture, tokenStore, capabilityReporter, nodeKeyRegistry, timeProvider: timeProvider);

        await connection.ConnectAsync();
        await connection.SendHeartbeatAsync(clientNodeId);

        var heartbeat = await fixture.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(clientNodeId, heartbeat.ClientNodeId);
        AssertEx.Equal(heartbeatTimestamp, heartbeat.Timestamp);
    }

    [Test]
    public async Task SendAsync_WhenDisconnected_ThrowsInvalidOperation()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var clientNodeId = Guid.NewGuid();
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, DateTimeOffset.UtcNow.AddMinutes(30));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture, tokenStore, capabilityReporter, nodeKeyRegistry);

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() => connection.SendHeartbeatAsync(clientNodeId));
        AssertEx.Contains(exception.Message, "Worker hub connection is not active.");
    }

    [Test]
    public async Task OnReconnected_WhenConnectionDrops_ReSendsWorkerHelloAndReportsCapabilities()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var clientNodeId = Guid.NewGuid();
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, DateTimeOffset.UtcNow.AddHours(1));
        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture,
            tokenStore,
            capabilityReporter,
            nodeKeyRegistry,
            configureHttpOptions: CreateFixtureWebSocketsOptionsConfigurator(fixture));

        await connection.ConnectAsync();
        AssertEx.Equal(WorkerConnectionState.Connected, connection.State);

        // Drain the handshake side-effects produced by the initial connect so the post-reconnect
        // assertions observe only the second handshake.
        var firstHello = await fixture.WaitForWorkerHelloAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(clientNodeId, firstHello);
        await capabilityReporter.Received(1).ReportToApiAsync(Arg.Any<CancellationToken>());

        // Wire a deterministic signal for the post-reconnect Connected transition. The connection
        // re-enters Connected only after OnReconnectedAsync completes its full re-handshake. A
        // transport-level drop (no graceful close frame) is required so WithAutomaticReconnect engages.
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += (_, args) =>
        {
            if (args.PreviousState == WorkerConnectionState.Reconnecting
                && args.CurrentState == WorkerConnectionState.Connected)
            {
                reconnected.TrySetResult();
            }
        };

        await fixture.FireTransportLevelConnectionDropAsync();

        await reconnected.Task.WaitAsync(TestBudgets.Contended);

        // OnReconnectedAsync re-sends WorkerHello over the hub and re-reports capabilities via the
        // capability reporter. The hub observes the second WorkerHello; the reporter observes the
        // second ReportToApiAsync call (once from the initial connect, once from the reconnect).
        var secondHello = await fixture.WaitForWorkerHelloAsync(TimeSpan.FromSeconds(5));
        AssertEx.Equal(clientNodeId, secondHello);

        await capabilityReporter.Received(2).ReportToApiAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OnReconnected_WhenCredentialsRevoked_StopsAndTransitionsToError()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var clientNodeId = Guid.NewGuid();

        // The token sits inside the 5-minute refresh skew (expires in 1 minute, not yet expired) so the
        // AccessTokenProvider attempts a refresh on EVERY hub authentication, including each reconnect.
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, DateTimeOffset.UtcNow.AddMinutes(1));

        // Initial connect must succeed, so the refresh reports Success up front; once the worker is
        // Connected the test flips the gate to CredentialsRevoked BEFORE dropping the transport. The flip
        // happens-before the drop on the test thread, so the post-drop reconnect deterministically sees
        // the revoked outcome (no timers, no SignalR call-count coupling).
        var refreshService = new GatedWorkerTokenRefreshService(WorkerTokenRefreshOutcome.Success);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture,
            tokenStore,
            capabilityReporter,
            nodeKeyRegistry,
            refreshService,
            configureHttpOptions: CreateFixtureWebSocketsOptionsConfigurator(fixture));

        var errorReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectingObserved = false;
        var reconnectAttemptsAfterError = 0;
        var errorAlreadySignaled = false;
        connection.StateChanged += (_, args) =>
        {
            if (args.CurrentState == WorkerConnectionState.Reconnecting)
            {
                reconnectingObserved = true;
                if (errorAlreadySignaled)
                {
                    Interlocked.Increment(ref reconnectAttemptsAfterError);
                }
            }

            if (args.CurrentState == WorkerConnectionState.Error)
            {
                errorAlreadySignaled = true;
                errorReached.TrySetResult();
            }
        };

        await connection.ConnectAsync();
        AssertEx.Equal(WorkerConnectionState.Connected, connection.State);

        // Now revoke: every subsequent refresh (including the reconnect's AccessTokenProvider call)
        // yields CredentialsRevoked, which surfaces as WorkerCredentialsRevokedException.
        refreshService.SetOutcome(WorkerTokenRefreshOutcome.CredentialsRevoked);

        await fixture.FireTransportLevelConnectionDropAsync();

        await errorReached.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(WorkerConnectionState.Error, connection.State);
        AssertEx.True(reconnectingObserved, "Expected the connection to attempt at least one reconnect before erroring.");

        // After Error there must be no further reconnect cycle: the policy returned null, so SignalR
        // stopped retrying. Give any stray reconnect a brief deterministic window to (not) appear.
        await AssertEx.EventuallyAsync(() => connection.State == WorkerConnectionState.Error,
            TimeSpan.FromMilliseconds(500),
            "Connection did not settle in Error.");
        AssertEx.Equal(expected: 0, Volatile.Read(ref reconnectAttemptsAfterError));
    }

    [Test]
    public async Task Reconnect_WhenTransientRefreshFailure_KeepsReconnecting()
    {
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var clientNodeId = Guid.NewGuid();
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, DateTimeOffset.UtcNow.AddMinutes(1));

        // Success at connect, transient failure afterwards. A transient failure must NOT stop the
        // reconnect loop: the provider returns "no valid token" (InvalidOperationException, not the
        // revoked type), the policy keeps issuing delays, and the worker stays in Reconnecting.
        var refreshService = new GatedWorkerTokenRefreshService(WorkerTokenRefreshOutcome.Success);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture,
            tokenStore,
            capabilityReporter,
            nodeKeyRegistry,
            refreshService,
            configureHttpOptions: CreateFixtureWebSocketsOptionsConfigurator(fixture));

        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorObserved = false;
        connection.StateChanged += (_, args) =>
        {
            if (args.CurrentState == WorkerConnectionState.Reconnecting)
            {
                reconnecting.TrySetResult();
            }

            if (args.CurrentState == WorkerConnectionState.Error)
            {
                errorObserved = true;
            }
        };

        await connection.ConnectAsync();
        AssertEx.Equal(WorkerConnectionState.Connected, connection.State);

        refreshService.SetOutcome(WorkerTokenRefreshOutcome.TransientFailure);

        await fixture.FireTransportLevelConnectionDropAsync();

        await reconnecting.Task.WaitAsync(TestBudgets.Contended);

        // The transient failure keeps the worker reconnecting; it must not transition to Error. Hold the
        // observation window open briefly to assert the negative (no Error) deterministically.
        await AssertEx.EventuallyAsync(() => connection.State == WorkerConnectionState.Reconnecting,
            TimeSpan.FromSeconds(2),
            "Expected the connection to remain in Reconnecting on a transient refresh failure.");
        AssertEx.False(errorObserved, "A transient refresh failure must not transition the worker to Error.");
        AssertEx.Equal(WorkerConnectionState.Reconnecting, connection.State);
    }

    [Test]
    public async Task OnReconnected_WhenTokenRefreshFails_TransitionsToError()
    {
        // Now reachable: a revoked refresh during reconnect surfaces WorkerCredentialsRevokedException,
        // the reconnect policy returns null, SignalR raises Closed with that exception, and
        // OnConnectionClosedAsync maps it to the Error (re-pairing required) state.
        await using var fixture = new FakeWorkerNodeFixture();
        await fixture.StartAsync();

        var clientNodeId = Guid.NewGuid();
        var tokenStore = MockTokenStore.Paired("test-access-token", clientNodeId, DateTimeOffset.UtcNow.AddMinutes(1));
        var refreshService = new GatedWorkerTokenRefreshService(WorkerTokenRefreshOutcome.Success);

        var capabilityReporter = Substitute.For<ICapabilityReporter>();
        capabilityReporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var nodeKeyRegistry = new NodeKeyRegistry(TimeProvider.System);

        await using var connection = CreateConnection(fixture,
            tokenStore,
            capabilityReporter,
            nodeKeyRegistry,
            refreshService,
            configureHttpOptions: CreateFixtureWebSocketsOptionsConfigurator(fixture));

        var errorReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.StateChanged += (_, args) =>
        {
            if (args.CurrentState == WorkerConnectionState.Error)
            {
                errorReached.TrySetResult();
            }
        };

        await connection.ConnectAsync();
        AssertEx.Equal(WorkerConnectionState.Connected, connection.State);

        refreshService.SetOutcome(WorkerTokenRefreshOutcome.CredentialsRevoked);

        await fixture.FireTransportLevelConnectionDropAsync();

        await errorReached.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(WorkerConnectionState.Error, connection.State);
    }

    private static WorkerHubConnection CreateConnection(FakeWorkerNodeFixture fixture,
        ITokenStore tokenStore,
        ICapabilityReporter capabilityReporter,
        INodeKeyRegistry nodeKeyRegistry,
        IWorkerTokenRefreshService? refreshService = null,
        TimeProvider? timeProvider = null,
        Action<HttpConnectionOptions>? configureHttpOptions = null)
    {
        var sender = new MockHubMessageSender();
        var deadLetterStore = Substitute.For<IDeadLetterStore>();
        deadLetterStore.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InvocationFailedPayload>>([]));

        var deadLetterFlushService = new DeadLetterFlushService(deadLetterStore,
            new Lazy<IHubMessageSender>(() => sender),
            NullLogger<DeadLetterFlushService>.Instance);

        return new WorkerHubConnection(tokenStore,
            Options.Create(new CentralPlatformOptions
            {
                BaseUrl = fixture.HubBaseUri.ToString(),
                HubPath = fixture.HubPath
            }),
            new ConnectionState(),
            new Lazy<ICapabilityReporter>(() => capabilityReporter),
            deadLetterFlushService,
            nodeKeyRegistry,
            NullLogger<WorkerHubConnection>.Instance,
            configureHttpOptions ?? CreateFixtureHttpOptionsConfigurator(fixture),
            refreshService,
            timeProvider);
    }

    private static Action<HttpConnectionOptions> CreateFixtureHttpOptionsConfigurator(FakeWorkerNodeFixture fixture)
    {
        return httpOptions =>
        {
            httpOptions.Transports = HttpTransportType.LongPolling;
            httpOptions.HttpMessageHandlerFactory = innerHandler => ConfigureFixtureCertificateValidation(innerHandler, fixture);
        };
    }

    // Reconnect tests MUST run over WebSockets: a server-side HubCallerContext.Abort() only surfaces
    // to the client as a transport loss on WebSockets, not on LongPolling. Over LongPolling the client
    // stays Connected and WithAutomaticReconnect never engages, so Reconnected/OnReconnectedAsync never run.
    private static Action<HttpConnectionOptions> CreateFixtureWebSocketsOptionsConfigurator(FakeWorkerNodeFixture fixture)
    {
        return httpOptions =>
        {
            if (fixture.ServerCert is null)
            {
                throw new InvalidOperationException("The fake worker node fixture certificate is not available.");
            }

            var expectedThumbprint = fixture.ServerCert.Thumbprint;

            httpOptions.Transports = HttpTransportType.WebSockets;

            // The negotiate request still travels over HTTP, so the self-signed cert must be trusted there too.
            httpOptions.HttpMessageHandlerFactory = innerHandler => ConfigureFixtureCertificateValidation(innerHandler, fixture);

            httpOptions.WebSocketConfiguration = ws =>
                ws.RemoteCertificateValidationCallback = (_, certificate, _, sslPolicyErrors) =>
                    sslPolicyErrors is SslPolicyErrors.None
                    || string.Equals(certificate?.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);
        };
    }

    private static HttpMessageHandler ConfigureFixtureCertificateValidation(HttpMessageHandler innerHandler, FakeWorkerNodeFixture fixture)
    {
        if (fixture.ServerCert is null)
        {
            throw new InvalidOperationException("The fake worker node fixture certificate is not available.");
        }

        if (innerHandler is not HttpClientHandler httpClientHandler)
        {
            return innerHandler;
        }

        var expectedThumbprint = fixture.ServerCert.Thumbprint;
        httpClientHandler.ServerCertificateCustomValidationCallback = (_, certificate, _, sslPolicyErrors) =>
            sslPolicyErrors is SslPolicyErrors.RemoteCertificateChainErrors
            && string.Equals(certificate?.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);

        return httpClientHandler;
    }

    private static void AssertEncryptedRuntimePackageEqual(EncryptedRuntimePackageDto expected, EncryptedRuntimePackageDto actual)
    {
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertEx.Equal(expected.AgentDefinitionVersion, actual.AgentDefinitionVersion);
        AssertEx.Equal(expected.ResolvedSystemPrompt, actual.ResolvedSystemPrompt);
        AssertEx.Equal(expected.AllowedTools.Count, actual.AllowedTools.Count);
        AssertEx.True(string.Equals(expected.ModelProfile, actual.ModelProfile, StringComparison.Ordinal));

        for (var index = 0; index < expected.AllowedTools.Count; index++)
        {
            var expectedTool = expected.AllowedTools[index];
            var actualTool = actual.AllowedTools[index];

            AssertEx.Equal(expectedTool.Name, actualTool.Name);
            AssertEx.True(string.Equals(expectedTool.Description, actualTool.Description, StringComparison.Ordinal));
            AssertEx.True(string.Equals(expectedTool.Schema, actualTool.Schema, StringComparison.Ordinal));
        }

        AssertEx.Equal(expected.Timeouts.InvocationTimeoutSeconds, actual.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(expected.Timeouts.ToolCallTimeoutSeconds, actual.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected.Timeouts.StreamIdleTimeoutSeconds, actual.Timeouts.StreamIdleTimeoutSeconds);
        AssertEx.Equal(expected.ConfigHash, actual.ConfigHash);
        AssertEx.Equal(expected.ConversationContextHash, actual.ConversationContextHash);
        AssertEx.Equal(expected.ConversationContext.Count, actual.ConversationContext.Count);

        for (var index = 0; index < expected.ConversationContext.Count; index++)
        {
            var expectedMessage = expected.ConversationContext[index];
            var actualMessage = actual.ConversationContext[index];

            AssertEx.Equal(expectedMessage.Id, actualMessage.Id);
            AssertEx.Equal(expectedMessage.Role, actualMessage.Role);
            AssertEx.Equal(expectedMessage.SortOrder, actualMessage.SortOrder);
            AssertEx.Equal(expectedMessage.EpochVersion, actualMessage.EpochVersion);
            AssertEx.Equal(expectedMessage.Aad, actualMessage.Aad);
            AssertReadOnlyMemoryEqual(expectedMessage.NodeWrappedEpochKey, actualMessage.NodeWrappedEpochKey);
            AssertReadOnlyMemoryEqual(expectedMessage.ClientEphemeralPublicKey, actualMessage.ClientEphemeralPublicKey);
            AssertReadOnlyMemoryEqual(expectedMessage.Ciphertext, actualMessage.Ciphertext);
            AssertReadOnlyMemoryEqual(expectedMessage.ContentIv, actualMessage.ContentIv);
        }

        AssertReadOnlyMemoryEqual(expected.NodeWrappedEpochKey, actual.NodeWrappedEpochKey);
        AssertReadOnlyMemoryEqual(expected.ClientEphemeralPublicKey, actual.ClientEphemeralPublicKey);
        AssertReadOnlyMemoryEqual(expected.Ciphertext, actual.Ciphertext);
        AssertReadOnlyMemoryEqual(expected.ContentIv, actual.ContentIv);
        AssertEx.Equal(expected.Aad, actual.Aad);
        AssertEx.Equal(expected.InvocationId, actual.InvocationId);
        AssertEx.Equal(expected.ClientNodeId, actual.ClientNodeId);
    }

    private static void AssertEncryptedChunkEqual(EncryptedChunkEnvelopeV1 expected, EncryptedChunkEnvelopeV1 actual)
    {
        AssertEx.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertReadOnlyMemoryEqual(expected.ChunkIv, actual.ChunkIv);
        AssertReadOnlyMemoryEqual(expected.ChunkCiphertext, actual.ChunkCiphertext);
        AssertEx.Equal(expected.Sequence, actual.Sequence);
    }

    private static void AssertEncryptedCompletedEqual(EncryptedCompletedEnvelopeV1 expected, EncryptedCompletedEnvelopeV1 actual)
    {
        AssertEx.Equal(expected.ProtocolVersion, actual.ProtocolVersion);
        AssertEx.Equal(expected.ConversationId, actual.ConversationId);
        AssertEx.Equal(expected.MessageId, actual.MessageId);
        AssertEx.Equal(expected.EpochVersion, actual.EpochVersion);
        AssertReadOnlyMemoryEqual(expected.FinalIv, actual.FinalIv);
        AssertReadOnlyMemoryEqual(expected.FinalCiphertext, actual.FinalCiphertext);
        AssertEx.Equal(expected.TotalSequence, actual.TotalSequence);
        AssertEx.Equal(expected.TokenCounts.Count, actual.TokenCounts.Count);

        foreach (var expectedTokenCount in expected.TokenCounts)
        {
            AssertEx.True(actual.TokenCounts.TryGetValue(expectedTokenCount.Key, out var actualValue));
            AssertEx.Equal(expectedTokenCount.Value, actualValue);
        }
    }

    private static void AssertReadOnlyMemoryEqual(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual)
    {
        AssertEx.True(expected.Span.SequenceEqual(actual.Span));
    }

    private sealed class GatedWorkerTokenRefreshService : IWorkerTokenRefreshService
    {
        private readonly Lock _gate = new();
        private WorkerTokenRefreshOutcome _outcome;

        public GatedWorkerTokenRefreshService(WorkerTokenRefreshOutcome initialOutcome)
        {
            _outcome = initialOutcome;
        }

        public Task<WorkerTokenRefreshOutcome> TryRefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_outcome);
            }
        }

        public void SetOutcome(WorkerTokenRefreshOutcome outcome)
        {
            lock (_gate)
            {
                _outcome = outcome;
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}
