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
    public async Task DispatchInvocationAssignedAsync_WhenPayloadCannotDeserialize_ThrowsJsonException()
    {
        var runner = Substitute.For<IInvocationRunner>();
#pragma warning disable CA2000
        var nodeKeyRegistry = new FakeNodeKeyRegistry();
#pragma warning restore CA2000
        var envelopeCryptoService = new FakeEnvelopeCryptoService(_ => new byte[] { 1, 2, 3 });
        var sender = new MockHubMessageSender();

        var dispatcher = new WorkerEventDispatcher(runner,
            envelopeCryptoService,
            new Lazy<IHubMessageSender>(() => sender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);

        var exception = await AssertEx.ThrowsAsync<JsonException>(() => dispatcher.DispatchInvocationAssignedAsync(new EncryptedRuntimePackageDto
        {
            ConversationId = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            EpochVersion = 1,
            NodeWrappedEpochKey = new byte[] { 1 },
            ClientEphemeralPublicKey = new byte[] { 2 },
            Ciphertext = new byte[] { 3 },
            ContentIv = new byte[] { 4 },
            Aad = new byte[36],
            InvocationId = Guid.NewGuid()
        }));

        AssertEx.Contains(exception.Message, "invalid start of a value", StringComparison.OrdinalIgnoreCase);
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
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
        var envelopeCryptoService = new FakeEnvelopeCryptoService(_ => throw new InvalidOperationException("Encrypted runtime package AAD did not match the expected envelope metadata."));

        var dispatcher = new WorkerEventDispatcher(runner,
            envelopeCryptoService,
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
            new FakeEnvelopeCryptoService(_ => throw new InvalidOperationException("decrypt should not run for expired retired keys")),
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
        var envelopeCryptoService = new FakeEnvelopeCryptoService(encryptedPackage =>
        {
            var runtimePackage = DeserializeRuntimePackage(encryptedPackage);
            return JsonSerializer.SerializeToUtf8Bytes(runtimePackage, SerializerOptions);
        });

        return CreateDispatcher(runner, envelopeCryptoService, sender, nodeKeyRegistry);
    }

    private static WorkerEventDispatcher CreateDispatcher(IInvocationRunner runner,
        IEnvelopeCryptoService envelopeCryptoService,
        IHubMessageSender hubMessageSender,
        INodeKeyRegistry nodeKeyRegistry)
    {
        return new WorkerEventDispatcher(runner,
            envelopeCryptoService,
            new Lazy<IHubMessageSender>(() => hubMessageSender),
            nodeKeyRegistry,
            NullLogger<WorkerEventDispatcher>.Instance);
    }

    private static EncryptedRuntimePackageDto CreateEncryptedPackage(RuntimePackage runtimePackage)
    {
        return new EncryptedRuntimePackageDto
        {
            ConversationId = runtimePackage.ConversationId,
            MessageId = Guid.NewGuid(),
            EpochVersion = 1,
            NodeWrappedEpochKey = new byte[] { 1, 2, 3 },
            ClientEphemeralPublicKey = new byte[] { 4, 5, 6 },
            Ciphertext = JsonSerializer.SerializeToUtf8Bytes(runtimePackage, SerializerOptions),
            ContentIv = new byte[] { 7, 8, 9 },
            Aad = new byte[36],
            InvocationId = runtimePackage.InvocationId,
            ClientNodeId = runtimePackage.ClientNodeId
        };
    }

    private static RuntimePackage DeserializeRuntimePackage(EncryptedRuntimePackageDto encryptedPackage)
    {
        var invocationId = encryptedPackage.InvocationId ?? Guid.NewGuid();
        var conversationId = encryptedPackage.ConversationId;
        var clientNodeId = encryptedPackage.ClientNodeId ?? Guid.NewGuid();

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

    private sealed class FakeEnvelopeCryptoService : IEnvelopeCryptoService
    {
        private readonly Func<EncryptedRuntimePackageDto, byte[]> _decrypt;

        public FakeEnvelopeCryptoService(Func<EncryptedRuntimePackageDto, byte[]> decrypt)
        {
            _decrypt = decrypt;
        }

        public EnvelopeDecryptionResult DecryptRuntimePackage(EncryptedRuntimePackageDto package, Key nodePrivateKey)
        {
            return new EnvelopeDecryptionResult(_decrypt(package), new byte[32]);
        }

        public EncryptedChunkEnvelopeV1 EncryptChunk(Guid conversationId,
            Guid messageId,
            int epochVersion,
            ReadOnlySpan<byte> epochKey,
            ReadOnlySpan<byte> plaintext,
            long sequence)
        {
            throw new NotSupportedException();
        }

        public EncryptedCompletedEnvelopeV1 EncryptCompleted(Guid conversationId,
            Guid messageId,
            int epochVersion,
            ReadOnlySpan<byte> epochKey,
            ReadOnlySpan<byte> plaintext,
            long totalSequence,
            IReadOnlyDictionary<string, long> tokenCounts)
        {
            throw new NotSupportedException();
        }
    }
}
