namespace XE_Local_AI_Engine.Tests.Chat;

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Tests.CodexOAuth;

/// <summary>
///     A maintenance dispatcher and worker over an in-memory container: a substituted persistence read, an estimator
///     with a fixed projection and correction, and a per-scope recording compaction service.
/// </summary>
internal sealed class ConversationMaintenanceHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public ConversationMaintenanceHarness(ConversationCompactionOptions? options = null, int projectedTokens = 100_000, double observedCorrection = 1.0,
        TimeProvider? timeProvider = null)
    {
        var accessor = Options.Create(options ?? new ConversationCompactionOptions());
        Estimator = new FixedTokenEstimator(projectedTokens, observedCorrection);
        Persistence.GetConversationForTurnAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                   .Returns(call => Task.FromResult<NodeChatConversationDto?>(Conversation(call.Arg<Guid>())));

        var services = new ServiceCollection();
        _ = services.AddSingleton(Persistence);
        _ = services.AddSingleton<ITokenEstimator>(Estimator);
        _ = services.AddScoped<IConversationCompactionService>(_ => new RecordingCompactionService(this));
        _ = services.AddScoped<IConversationStateDistillationService>(_ => new RecordingDistillationService(this));
        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });

        Dispatcher = new ConversationMaintenanceDispatcher(accessor, new CapturingLogger<ConversationMaintenanceDispatcher>());
        Worker = new ConversationMaintenanceWorker(_provider.GetRequiredService<IServiceScopeFactory>(), Dispatcher, accessor, timeProvider ?? TimeProvider.System, Logger);
    }

    public INodeChatPersistenceService Persistence { get; } = Substitute.For<INodeChatPersistenceService>();

    public FixedTokenEstimator Estimator { get; }

    public ConversationMaintenanceDispatcher Dispatcher { get; }

    public ConversationMaintenanceWorker Worker { get; }

    public CapturingLogger<ConversationMaintenanceWorker> Logger { get; } = new();

    /// <summary>Every compaction the worker asked for, with the scoped service instance that served it.</summary>
    public ConcurrentQueue<(Guid ConversationId, string? RequestedModel, RecordingCompactionService Instance)> Compactions { get; } = new();

    /// <summary>Every distillation the worker asked for.</summary>
    public ConcurrentQueue<Guid> Distillations { get; } = new();

    /// <summary>Runs inside each compaction before it returns; a test parks, throws or observes the token here.</summary>
    public Func<Guid, CancellationToken, Task>? OnCompact { get; set; }

    public static ConversationMaintenanceJob Job(Guid conversationId, string? modelName = "local-model", int capacity = 8_192, int reserved = 1_024,
        ConversationMaintenanceKind kind = ConversationMaintenanceKind.Compact) =>
        new()
        {
            ConversationId = conversationId,
            Kind = kind,
            ModelName = modelName,
            ContextCapacityTokens = capacity,
            ReservedOutputTokens = reserved
        };

    public async ValueTask DisposeAsync()
    {
        Worker.Dispose();
        await _provider.DisposeAsync();
    }

    private static NodeChatConversationDto Conversation(Guid conversationId) =>
        new()
        {
            ConversationId = conversationId,
            Title = null,
            UserId = null,
            CreatedAtUtc = 0,
            LastSeenUtc = 0,
            Purged = false,
            Messages =
            [
                new NodeChatPersistedMessageDto
                {
                    MessageId = Guid.NewGuid(),
                    ConversationId = conversationId,
                    RequestId = null,
                    Sequence = 0,
                    Role = "user",
                    Content = "hello",
                    Reasoning = null,
                    Status = NodeChatMessageStatusValues.Completed,
                    CreatedAtUtc = 0,
                    UpdatedAtUtc = 0,
                    Model = null,
                    Error = null,
                    MetadataJson = null
                }
            ]
        };

    /// <summary>Answers every projection with a fixed count and a fixed observed correction, recording the model it was asked for.</summary>
    internal sealed class FixedTokenEstimator : ITokenEstimator
    {
        private readonly int _projectedTokens;
        private readonly double _observedCorrection;

        public FixedTokenEstimator(int projectedTokens, double observedCorrection)
        {
            _projectedTokens = projectedTokens;
            _observedCorrection = observedCorrection;
        }

        public string? LastModelName { get; private set; }

        public int EstimateTokens(ChatMessage message) =>
            _projectedTokens;

        public int EstimateTokens(IReadOnlyList<ChatMessage> messages) =>
            _projectedTokens;

        public int EstimateTokens(IReadOnlyList<ChatMessage> messages, string? modelName)
        {
            LastModelName = modelName;
            return _projectedTokens;
        }

        public double ResolveObservedCorrection(string? modelName) =>
            _observedCorrection;
    }

    /// <summary>
    ///     Records each call; hand-written because the worker calls the three-argument default-interface overload,
    ///     which forwards here.
    /// </summary>
    internal sealed class RecordingCompactionService : IConversationCompactionService
    {
        private readonly ConversationMaintenanceHarness _harness;

        public RecordingCompactionService(ConversationMaintenanceHarness harness)
        {
            _harness = harness;
        }

        public async Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
            string? requestedModel,
            int? recentMessagesToKeepVerbatim,
            bool distill = true,
            CancellationToken cancellationToken = default)
        {
            _harness.Compactions.Enqueue((conversationId, requestedModel, this));
            if (_harness.OnCompact is { } onCompact)
            {
                await onCompact(conversationId, cancellationToken);
            }

            return new ConversationCompactionResult
            {
                Outcome = ConversationCompactionOutcome.Compacted,
                MessagesFolded = 1
            };
        }
    }

    internal sealed class RecordingDistillationService : IConversationStateDistillationService
    {
        private readonly ConversationMaintenanceHarness _harness;

        public RecordingDistillationService(ConversationMaintenanceHarness harness)
        {
            _harness = harness;
        }

        public Task<ConversationStateDistillationOutcome> DistillPendingAsync(Guid conversationId, string? requestedModel, int? upToAnchorSequence,
            CancellationToken cancellationToken = default)
        {
            _harness.Distillations.Enqueue(conversationId);
            return Task.FromResult(new ConversationStateDistillationOutcome
            {
                Status = ConversationStateDistillationStatus.Distilled,
                Calls = 1
            });
        }
    }
}
