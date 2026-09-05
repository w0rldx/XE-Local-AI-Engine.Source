namespace XE_Local_AI_Engine.Tests.Integrations;

using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The accept path under test, wired to in-memory stores rather than mocks so the ORDER of its writes is
///     observable: admission commits first, then the owned conversation and the seed. Shared by the invocation-service
///     suite and the caller-managed session suites, so both drive ONE accept path rather than two that can disagree.
/// </summary>
internal sealed class IntegrationInvokeHarness
{
    public const string KeyPrefix = "xeint_aaaaaaaa";

    public const string RotatedKeyPrefix = "xeint_bbbbbbbb";

    private readonly List<IntegrationApiKeySnapshot> _keys;
    private readonly IIntegrationApiKeyStore _keyStore;

    public IntegrationInvokeHarness(int maxQueued = 8,
        int maxQueuedPerPrincipal = 8,
        int maxSeedBytes = 262_144,
        int maxTracked = 64,
        int queueCapacity = 8)
    {
        PrincipalId = Guid.NewGuid();
        _keys =
        [
            new IntegrationApiKeySnapshot(Guid.NewGuid(),
                PrincipalId,
                KeyPrefix,
                ReadOnlyMemory<byte>.Empty,
                "primary",
                AllowedTriggerIdsJson: null,
                CreatedAtUtc: 1,
                LastUsedAtUtc: null,
                RevokedAtUtc: null)
        ];

        _keyStore = Substitute.For<IIntegrationApiKeyStore>();
        _ = _keyStore.GetByPrefixAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                     .Returns(call => _keys.SingleOrDefault(row => string.Equals(row.KeyPrefix, call.Arg<string>(), StringComparison.Ordinal)));

        Buffer = new IntegrationExecutionEventBuffer(Options.Create(new IntegrationOptions
            {
                MaxTrackedExecutions = maxTracked
            }),
            TimeProvider.System);

        Queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        Persistence = Substitute.For<INodeChatPersistenceService>();

        // The execution store's continuation bump reaches the session rows, which is what makes ExecutionCount and the
        // IntegrationSessionUnavailableException backstop observable rather than assumed.
        Executions.Sessions = Sessions;

        Access = new IntegrationExternalAccess(Executions, Sessions, _keyStore);
        SessionGate = new IntegrationSessionGate();
        SessionService = new IntegrationSessionService(Sessions,
            Executions,
            Triggers,
            Access,
            Persistence,
            SessionGate,
            TimeProvider.System,
            NullLogger<IntegrationSessionService>.Instance);

        Service = new IntegrationInvocationService(Triggers,
            _keyStore,
            Substitute.For<IIntegrationApiKeyService>(),
            Executions,
            Buffer,
            Persistence,
            SessionService,
            SessionGate,
            Queue,
            Options.Create(new IntegrationOptions
            {
                MaxQueuedExecutions = maxQueued,
                MaxQueuedExecutionsPerPrincipal = maxQueuedPerPrincipal,
                MaxSeedBytes = maxSeedBytes,
                MaxTrackedExecutions = maxTracked
            }),
            TimeProvider.System,
            NullLogger<IntegrationInvocationService>.Instance);
    }

    public IntegrationExecutionEventBuffer Buffer { get; }

    public FakeIntegrationExecutionStore Executions { get; } = new();

    public FakeIntegrationSessionStore Sessions { get; } = new();

    public IntegrationExternalAccess Access { get; }

    public IntegrationSessionGate SessionGate { get; }

    public IntegrationSessionService SessionService { get; }

    public INodeChatPersistenceService Persistence { get; }

    public Guid PrincipalId { get; }

    public Channel<Guid> Queue { get; }

    public IIntegrationInvocationService Service { get; }

    public FakeIntegrationTriggerStore Triggers { get; } = new();

    public IntegrationTriggerSnapshot SeedTrigger(string name,
        bool enabled = true,
        IntegrationSessionPolicy sessionPolicy = IntegrationSessionPolicy.PerInvocation,
        IntegrationInputKinds acceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json) =>
        Triggers.Seed(name, Guid.NewGuid(), enabled, sessionPolicy, acceptedInputKinds);

    public void RestrictKeyTo(Guid triggerId) =>
        _keys[0] = _keys[0] with
        {
            AllowedTriggerIdsJson = $"[\"{triggerId}\"]"
        };

    public void RevokeKey() =>
        _keys[0] = _keys[0] with
        {
            RevokedAtUtc = 2
        };

    /// <summary>Issues a second credential for the SAME integrator, which is the rotation case ownership must survive.</summary>
    public void RotateCredential() =>
        _keys.Add(_keys[0] with
        {
            Id = Guid.NewGuid(),
            KeyPrefix = RotatedKeyPrefix,
            Label = "rotated"
        });

    /// <summary>Seeds a session an earlier accept would have created, owned by THIS harness's principal by default.</summary>
    public IntegrationSessionSnapshot SeedSession(Guid triggerId,
        Guid? principalId = null,
        IntegrationSessionStatus status = IntegrationSessionStatus.Active,
        Guid? conversationId = null,
        long lastActivityUtc = 0,
        int executionCount = 1) =>
        Sessions.Seed(Guid.NewGuid(),
            triggerId,
            conversationId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            principalId ?? PrincipalId,
            status,
            lastActivityUtc,
            executionCount);

    public IntegrationCallerIdentity Caller(string keyPrefix = KeyPrefix) =>
        new(PrincipalId, keyPrefix);

    public static IntegrationInputDto Text(string text) =>
        new(IntegrationInputKinds.Text, text, Label: null, Json: null);

    public IReadOnlyList<NodeChatCreateConversationRequest> CapturedConversations() =>
    [
        .. Persistence.ReceivedCalls()
                      .Where(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.CreateConversationAsync))
                      .Select(static call => (NodeChatCreateConversationRequest)call.GetArguments()[0]!)
    ];

    public IReadOnlyList<NodeChatPersistUserMessageRequest> CapturedSeeds() =>
    [
        .. Persistence.ReceivedCalls()
                      .Where(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.PersistUserMessageAsync))
                      .Select(static call => (NodeChatPersistUserMessageRequest)call.GetArguments()[0]!)
    ];

    public NodeChatCreateConversationRequest CapturedConversation() =>
        (NodeChatCreateConversationRequest)Persistence.ReceivedCalls()
                                                      .Single(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.CreateConversationAsync))
                                                      .GetArguments()[0]!;

    public NodeChatPersistUserMessageRequest CapturedSeed() =>
        (NodeChatPersistUserMessageRequest)Persistence.ReceivedCalls()
                                                      .Single(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.PersistUserMessageAsync))
                                                      .GetArguments()[0]!;

    public Task<IntegrationAcceptResult> AcceptAsync(string triggerName,
        IReadOnlyList<IntegrationInputDto>? inputs = null,
        Guid? requestId = null,
        byte[]? rawBody = null,
        Guid? sessionId = null,
        Guid? principalId = null,
        string keyPrefix = KeyPrefix,
        CancellationToken cancellationToken = default)
    {
        return Service.AcceptAsync(Request(triggerName, inputs, requestId, rawBody, sessionId, principalId, keyPrefix), cancellationToken);
    }

    private IntegrationAcceptRequest Request(string triggerName,
        IReadOnlyList<IntegrationInputDto>? inputs,
        Guid? requestId,
        byte[]? rawBody,
        Guid? sessionId,
        Guid? principalId,
        string keyPrefix)
    {
        if (principalId is { } stranger)
        {
            // A different integrator presenting its own credential, which is what makes the (principal, request id)
            // scoping observable at all.
            var strangerPrefix = $"xeint_{stranger:N}"[..14];
            if (!_keys.Any(row => string.Equals(row.KeyPrefix, strangerPrefix, StringComparison.Ordinal)))
            {
                _keys.Add(_keys[0] with
                {
                    Id = Guid.NewGuid(),
                    PrincipalId = stranger,
                    KeyPrefix = strangerPrefix,
                    Label = "stranger",
                    RevokedAtUtc = null
                });
            }

            keyPrefix = strangerPrefix;
        }

        return new IntegrationAcceptRequest(triggerName,
            principalId ?? PrincipalId,
            keyPrefix,
            requestId ?? Guid.NewGuid(),
            sessionId,
            inputs ?? [Text("do the thing")],
            rawBody ?? Encoding.UTF8.GetBytes("""{"inputs":[]}"""));
    }
}
