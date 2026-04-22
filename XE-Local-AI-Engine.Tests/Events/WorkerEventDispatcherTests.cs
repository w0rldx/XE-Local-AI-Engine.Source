namespace XE_Local_AI_Engine.Tests.Events;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using NSec.Cryptography;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimeEnvelope;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class WorkerEventDispatcherTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Test]
    public void CurrentInvocation_Initially_IsNull()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());

        AssertEx.Null(dispatcher.CurrentInvocation);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_CallsRunnerRunAsync()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        await runner.Received(1).RunAsync(Arg.Is<InvocationExecutionContext>(context => context.Package.InvocationId == package.InvocationId
                                                                              && context.Package.ConversationId == package.ConversationId
                                                                              && context.Package.ClientNodeId == package.ClientNodeId
                                                                              && context.MessageId != Guid.Empty
                                                                              && context.EpochVersion == 1
                                                                              && context.EpochKey.Length == 32),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenAlreadyBusy_LogsAndDrops()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>()).Returns(_ => gate.Task);

        var dispatcher = CreateDispatcher(runner);
        var first = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();
        var second = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).Build();

        var firstDispatch = dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(first));
        await Task.Delay(20);
        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(second));

        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(first.InvocationId, dispatcher.CurrentInvocation?.InvocationId ?? Guid.Empty);

        gate.SetResult();
        await firstDispatch;
    }

    [Test]
    public async Task ReportInvocationThinkingChunkAsync_AccumulatesThinkingContentSeparately()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.ReportInvocationAssignedAsync(package);

        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, "Let me think...");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, "Hello");
        await dispatcher.ReportInvocationThinkingChunkAsync(package.InvocationId, " more thought");
        await dispatcher.ReportInvocationStreamChunkAsync(package.InvocationId, " world");

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal("Let me think... more thought", current.StreamedThinkingContent);
        AssertEx.Equal(2, current.StreamedThinkingChunkCount);
        AssertEx.Equal("Hello world", current.StreamedContent);
        AssertEx.Equal(2, current.StreamedChunkCount);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_SetsCurrentInvocation()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(package.ConversationId, current.ConversationId);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_RaisesInvocationStateChangedEvent()
    {
        var dispatcher = CreateDispatcher(Substitute.For<IInvocationRunner>());
        var package = RuntimePackageBuilder.Valid().Build();
        var eventCount = 0;
        dispatcher.InvocationStateChanged += (_, _) => eventCount++;

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        AssertEx.True(eventCount >= 2);
    }

    [Test]
    public async Task DispatchToolCallResultAsync_CallsResolveToolCallResult()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var evt = new ToolCallResultEvent
        {
            RequestId = "req-1",
            Result = "ok"
        };

        await dispatcher.DispatchToolCallResultAsync(evt);

        runner.Received(1).ResolveToolCallResult(evt);
    }

    [Test]
    public async Task DispatchApprovalResolvedAsync_OnlyLogs_DoesNotCallRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);

        await dispatcher.DispatchApprovalResolvedAsync(new ApprovalResolvedEvent
        {
            RequestId = "req-1",
            Approved = true
        });

        runner.DidNotReceive().Cancel(Arg.Any<Guid>());
        runner.DidNotReceive().CancelAll();
        runner.DidNotReceive().ResolveToolCallResult(Arg.Any<ToolCallResultEvent>());
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationCancelledAsync_CallsCancelOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();
        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        await dispatcher.DispatchInvocationCancelledAsync(new InvocationCancelledEvent
        {
            InvocationId = package.InvocationId,
            Reason = "cancelled"
        });

        runner.Received(1).Cancel(package.InvocationId);
    }

    [Test]
    public async Task DispatchDisconnectRequestedAsync_CallsCancelAllOnRunner()
    {
        var runner = Substitute.For<IInvocationRunner>();
        var dispatcher = CreateDispatcher(runner);

        await dispatcher.DispatchDisconnectRequestedAsync(new DisconnectRequestedEvent
        {
            Reason = "shutdown"
        });

        runner.Received(1).CancelAll();
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenRunnerThrows_MarksInvocationFailed()
    {
        var runner = Substitute.For<IInvocationRunner>();
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromException(new InvalidOperationException("boom")));

        var dispatcher = CreateDispatcher(runner);
        var package = RuntimePackageBuilder.Valid().Build();

        await dispatcher.DispatchInvocationAssignedAsync(CreateEncryptedPackage(package));

        var current = AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(package.InvocationId, current.InvocationId);
        AssertEx.Equal(InvocationStatus.Failed, current.Status);
        AssertEx.Equal("boom", current.Error);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenAadMismatch_EmitsInvocationKeyMismatch()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var assembler = new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("Encrypted runtime package AAD did not match the expected envelope metadata."));

        var dispatcher = new WorkerEventDispatcher(runner,
            assembler,
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentKeyMismatches,
            mismatch => mismatch.MessageId == encryptedPackage.MessageId
                        && mismatch.Reason == "aad-mismatch"
                        && mismatch.NodeKeyIdUsed == "active-key");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenRetiredKeyExpired_EmitsInvocationKeyMismatch()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry(new NodeKeyResolution
        {
            RequestedKeyId = "retired-key-1",
            Status = NodeKeyLookupStatus.RetiredExpired,
            KeyIdUsed = "retired-key-1"
        });
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());

        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("assemble should not run for expired retired keys")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentKeyMismatches,
            mismatch => mismatch.MessageId == encryptedPackage.MessageId
                        && mismatch.Reason == "retired-key"
                        && mismatch.NodeKeyIdUsed == "retired-key-1");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner)
    {
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var assembler = new FakeRuntimePackageEnvelopeAssembler(encryptedPackage =>
        {
            var runtimePackage = DeserializeRuntimePackage(encryptedPackage);
            return InvocationExecutionContext.Create(runtimePackage,
                encryptedPackage.MessageId,
                encryptedPackage.EpochVersion,
                new byte[32]);
        });

        return CreateDispatcher(runner, assembler, sender, nodeKeyRegistry);
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner,
        IRuntimePackageEnvelopeAssembler assembler,
        IHubMessageSender hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry)
    {
        return new WorkerEventDispatcher(runner,
            assembler,
            new Lazy<IHubMessageSender>(() => hubMessageSender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenConfigHashMismatch_SendsEncryptedFailure()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("runtime-package-config-hash-mismatch")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == encryptedPackage.ConversationId
                       && failure.MessageId == encryptedPackage.MessageId
                       && failure.FailureCategory == nameof(FailureCategory.HashMismatch)
                       && failure.Error == "runtime-package-config-hash-mismatch");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenHistoryHashMismatch_SendsEncryptedFailure()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var encryptedPackage = CreateEncryptedPackage(RuntimePackageBuilder.Valid().Build());
        var dispatcher = new WorkerEventDispatcher(runner,
            new FakeRuntimePackageEnvelopeAssembler(_ => throw new InvalidOperationException("runtime-package-history-hash-mismatch")),
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        AssertEx.ContainsSingle(sender.SentEncryptedFailures,
            failure => failure.ConversationId == encryptedPackage.ConversationId
                       && failure.MessageId == encryptedPackage.MessageId
                       && failure.FailureCategory == nameof(FailureCategory.HashMismatch)
                       && failure.Error == "runtime-package-history-hash-mismatch");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DispatchInvocationAssignedAsync_WhenEnvelopeIsValid_BuildsRealRuntimePackageFromMixedEnvelope()
    {
        var runner = Substitute.For<IInvocationRunner>();
        InvocationExecutionContext? capturedContext = null;
        byte[]? capturedEpochKey = null;
        runner.RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.Arg<InvocationExecutionContext>();
                capturedEpochKey = capturedContext.EpochKey.ToArray();
                return Task.CompletedTask;
            });

#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var sender = new MockHubMessageSender();
        var historyEntryOne = CreateHistoryEntry(MessageRole.System, sortOrder: 10);
        var historyEntryTwo = CreateHistoryEntry(MessageRole.Assistant, sortOrder: 20);
        var encryptedPackage = CreateMixedEnvelopePackage([historyEntryOne, historyEntryTwo]);
        var expectedEpochKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var envelopeCryptoService = Substitute.For<IEnvelopeCryptoService>();
        envelopeCryptoService.DecryptConversationMessage(encryptedPackage.ConversationId, historyEntryOne, Arg.Any<Key>())
            .Returns(_ => new EnvelopeDecryptionResult("system guidance"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptConversationMessage(encryptedPackage.ConversationId, historyEntryTwo, Arg.Any<Key>())
            .Returns(_ => new EnvelopeDecryptionResult("assistant reply"u8.ToArray(), new byte[32]));
        envelopeCryptoService.DecryptRuntimePackage(encryptedPackage, Arg.Any<Key>())
            .Returns(_ => new EnvelopeDecryptionResult("latest user message"u8.ToArray(), expectedEpochKey.ToArray()));

        var validator = Substitute.For<IRuntimePackageValidator>();
        validator.Validate(Arg.Any<RuntimePackage>()).Returns(RuntimePackageValidationResult.Success);

        var assembler = new RuntimePackageEnvelopeAssembler(envelopeCryptoService, nodeKeyRegistry, validator);
        var dispatcher = CreateDispatcher(runner, assembler, sender, nodeKeyRegistry);

        await dispatcher.DispatchInvocationAssignedAsync(encryptedPackage);

        await runner.Received(1).RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());

        var context = AssertEx.NotNull(capturedContext);
        AssertEx.Equal(encryptedPackage.InvocationId, context.Package.InvocationId);
        AssertEx.Equal(encryptedPackage.ConversationId, context.Package.ConversationId);
        AssertEx.Equal(encryptedPackage.ClientNodeId, context.Package.ClientNodeId);
        AssertEx.Equal(encryptedPackage.AgentDefinitionVersion, context.Package.AgentDefinitionVersion);
        AssertEx.Equal(encryptedPackage.ResolvedSystemPrompt, context.Package.ResolvedSystemPrompt);
        AssertEx.True(string.Equals(encryptedPackage.ModelProfile, context.Package.ModelProfile, StringComparison.Ordinal));
        AssertEx.Equal(encryptedPackage.ConfigHash, context.Package.ConfigHash);
        AssertEx.Equal(encryptedPackage.Timeouts.InvocationTimeoutSeconds, context.Package.Timeouts.InvocationTimeoutSeconds);
        AssertEx.Equal(encryptedPackage.Timeouts.ToolCallTimeoutSeconds, context.Package.Timeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(encryptedPackage.Timeouts.StreamIdleTimeoutSeconds, context.Package.Timeouts.StreamIdleTimeoutSeconds);
        AssertEx.Equal(1, context.Package.AllowedTools.Count);
        AssertEx.Equal("open_url", context.Package.AllowedTools[0].Name);
        AssertEx.Equal(ToolLocation.ApiSide, context.Package.AllowedTools[0].Location);
        AssertEx.Equal("{\"type\":\"object\"}", context.Package.AllowedTools[0].ParameterSchema);
        AssertEx.Equal(3, context.Package.ConversationContext.Count);
        AssertEx.Equal(historyEntryOne.Id, context.Package.ConversationContext[0].Id);
        AssertEx.Equal(MessageRole.System, context.Package.ConversationContext[0].Role);
        AssertEx.Equal("system guidance", context.Package.ConversationContext[0].Content);
        AssertEx.Equal(10, context.Package.ConversationContext[0].SortOrder);
        AssertEx.Equal(historyEntryTwo.Id, context.Package.ConversationContext[1].Id);
        AssertEx.Equal(MessageRole.Assistant, context.Package.ConversationContext[1].Role);
        AssertEx.Equal("assistant reply", context.Package.ConversationContext[1].Content);
        AssertEx.Equal(20, context.Package.ConversationContext[1].SortOrder);
        AssertEx.Equal(encryptedPackage.MessageId, context.Package.ConversationContext[2].Id);
        AssertEx.Equal(MessageRole.User, context.Package.ConversationContext[2].Role);
        AssertEx.Equal("latest user message", context.Package.ConversationContext[2].Content);
        AssertEx.Equal(21, context.Package.ConversationContext[2].SortOrder);
        AssertEx.Equal(encryptedPackage.MessageId, context.MessageId);
        AssertEx.Equal(encryptedPackage.EpochVersion, context.EpochVersion);
        AssertEx.True((capturedEpochKey ?? []).SequenceEqual(expectedEpochKey));

        validator.Received(1).Validate(Arg.Is<RuntimePackage>(package =>
            package.ConversationContext.Count == 3
            && package.ConversationContext[2].Content == "latest user message"
            && package.ModelProfile == encryptedPackage.ModelProfile));
    }

    private static EncryptedRuntimePackageDto CreateEncryptedPackage(RuntimePackage runtimePackage)
    {
        return new EncryptedRuntimePackageDto
        {
            InvocationId = runtimePackage.InvocationId,
            ConversationId = runtimePackage.ConversationId,
            ClientNodeId = runtimePackage.ClientNodeId,
            MessageId = Guid.NewGuid(),
            EpochVersion = 1,
            AgentDefinitionVersion = runtimePackage.AgentDefinitionVersion,
            ResolvedSystemPrompt = runtimePackage.ResolvedSystemPrompt,
            AllowedTools = [],
            Timeouts = runtimePackage.Timeouts,
            ConfigHash = runtimePackage.ConfigHash,
            ConversationContext = [],
            ConversationContextHash = "4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945",
            NodeWrappedEpochKey = new byte[] { 1, 2, 3 },
            ClientEphemeralPublicKey = new byte[] { 4, 5, 6 },
            Ciphertext = JsonSerializer.SerializeToUtf8Bytes(runtimePackage, SerializerOptions),
            ContentIv = new byte[] { 7, 8, 9 },
            Aad = "message|aad-placeholder"
        };
    }

    private static EncryptedRuntimePackageDto CreateMixedEnvelopePackage(IReadOnlyList<EncryptedConversationMessageDto>? historyEntries = null)
    {
        var conversationContext = historyEntries?.ToList() ?? [];
        var package = new EncryptedRuntimePackageDto
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            ClientNodeId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 7,
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
                StreamIdleTimeoutSeconds = 30,
            },
            ConfigHash = string.Empty,
            ConversationContext = conversationContext,
            ConversationContextHash = string.Empty,
            NodeWrappedEpochKey = new byte[] { 1, 2, 3 },
            ClientEphemeralPublicKey = new byte[] { 4, 5, 6 },
            Ciphertext = new byte[] { 7, 8, 9 },
            ContentIv = new byte[] { 10, 11, 12 },
            Aad = "message|aad-placeholder"
        };

        return package with
        {
            ConfigHash = RuntimePackageConfigHash.Compute(package),
            ConversationContextHash = RuntimePackageHistoryHash.Compute(package.ConversationContext)
        };
    }

    private static EncryptedConversationMessageDto CreateHistoryEntry(MessageRole role, int sortOrder)
    {
        return new EncryptedConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = role,
            SortOrder = sortOrder,
            EpochVersion = 7,
            Aad = $"message|history-{sortOrder}",
            NodeWrappedEpochKey = new byte[] { 1, 2, 3 },
            ClientEphemeralPublicKey = new byte[] { 4, 5, 6 },
            Ciphertext = new byte[] { 7, 8, 9 },
            ContentIv = new byte[] { 10, 11, 12 }
        };
    }

    private static RuntimePackage DeserializeRuntimePackage(EncryptedRuntimePackageDto encryptedPackage)
    {
        var invocationId = encryptedPackage.InvocationId;
        var conversationId = encryptedPackage.ConversationId;
        var clientNodeId = encryptedPackage.ClientNodeId;

        return JsonSerializer.Deserialize<RuntimePackage>(encryptedPackage.Ciphertext.Span, SerializerOptions)
               ?? RuntimePackageBuilder.Valid()
                   .WithInvocationId(invocationId)
                   .WithConversationId(conversationId)
                   .WithClientNodeId(clientNodeId)
                   .Build();
    }

    private sealed class FakeNodeKeyRegistry : INodeKeyRegistry
    {
        private readonly Key _privateKey = Key.Create(KeyAgreementAlgorithm.X25519);
        private readonly NodeKeyResolution? _resolution;

        public FakeNodeKeyRegistry()
        {
        }

        public FakeNodeKeyRegistry(NodeKeyResolution resolution)
        {
            _resolution = resolution;
        }

        public string ActiveKeyId => "active-key";

        public PublicKey ActivePublicKey => _privateKey.PublicKey;

        public IReadOnlyList<NodeKeyResolution> ResolveGraceEligible()
        {
            return [_resolution ?? new NodeKeyResolution
            {
                RequestedKeyId = ActiveKeyId,
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = ActiveKeyId,
                PrivateKey = _privateKey,
                PublicKey = _privateKey.PublicKey
            }];
        }

        public NodeKeyResolution Resolve(string nodeKeyId)
        {
            if (_resolution is not null)
            {
                return _resolution;
            }

            return new NodeKeyResolution
            {
                RequestedKeyId = nodeKeyId,
                Status = NodeKeyLookupStatus.Active,
                KeyIdUsed = ActiveKeyId,
                PrivateKey = _privateKey,
                PublicKey = _privateKey.PublicKey
            };
        }

        public void Rotate(string nodeKeyId, Key privateKey)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
            _privateKey.Dispose();
        }
    }

    private sealed class FakeRuntimePackageEnvelopeAssembler : IRuntimePackageEnvelopeAssembler
    {
        private readonly Func<EncryptedRuntimePackageDto, InvocationExecutionContext> _assemble;

        public FakeRuntimePackageEnvelopeAssembler(Func<EncryptedRuntimePackageDto, InvocationExecutionContext> assemble)
        {
            _assemble = assemble;
        }

        public InvocationExecutionContext Assemble(EncryptedRuntimePackageDto package)
        {
            return _assemble(package);
        }
    }
}
