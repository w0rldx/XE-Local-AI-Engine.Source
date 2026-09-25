namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The distillation loop: one persisted state write per distiller call with an advancing watermark, a failed call
///     that never advances coverage, and the pending span taken in anchor space after the watermark.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ConversationStateDistillationServiceTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();

    [Test]
    public async Task DistillPendingAsync_PersistsAfterEveryCallWithAnAdvancingWatermark()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)));
        harness.Results.Enqueue(Distillation("first fact", consumed: 2, coversTo: 1));
        harness.Results.Enqueue(Distillation("second fact", consumed: 2, coversTo: 3));

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.Distilled, outcome.Status);
        AssertEx.Equal(expected: 2, outcome.Calls);
        AssertEx.Equal<int?>(3, outcome.CoversToSequence);
        AssertEx.Equal("0,1,2,3", Join(harness.InputSequences[0]));
        AssertEx.Equal("2,3", Join(harness.InputSequences[1]), "A call's consumed messages are dropped before the next call.");
        AssertEx.Equal(expected: 1, harness.InputStateEntryCounts[1], "The second call sees the state the first call produced.");

        var writes = harness.Writes();
        AssertEx.Equal("1,3", Join(writes.Select(static write => write.CoversToSequence ?? -1).ToList()));
        var finalState = AssertEx.NotNull(ConversationStateSerializer.Deserialize(writes[1].State));
        AssertEx.Equal("first fact,second fact", Join(finalState.Entries.Select(static entry => entry.Value).ToList()));
    }

    [Test]
    public async Task DistillPendingAsync_GuardsEveryWriteOnTheStampItLastSaw()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)) with
        {
            ConversationStateUpdatedAtUtc = 40
        });
        harness.Results.Enqueue(Distillation("first fact", consumed: 2, coversTo: 1));
        harness.Results.Enqueue(Distillation("second fact", consumed: 2, coversTo: 3));

        await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        var writes = harness.Writes();
        AssertEx.True(writes.All(static write => write.GuardUnchanged), "Every distillation write is a compare-and-set.");
        AssertEx.Equal<long?>(40, writes[0].ExpectedUpdatedAtUtc, "The first write expects the stamp the row carried when read.");
        AssertEx.Equal<long?>(writes[0].UpdatedAtUtc, writes[1].ExpectedUpdatedAtUtc, "The next write expects the stamp the previous write set.");
    }

    [Test]
    public async Task DistillPendingAsync_WhenAPathChangeRestampedTheRowMidCall_DiscardsThatDeltaAndStops()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 6)))
        {
            AcceptedWrites = 1
        };
        harness.Results.Enqueue(Distillation("first fact", consumed: 2, coversTo: 1));
        harness.Results.Enqueue(Distillation("stale fact", consumed: 2, coversTo: 3));
        harness.Results.Enqueue(Distillation("never reached", consumed: 2, coversTo: 5));

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.Superseded, outcome.Status);
        AssertEx.Equal(expected: 1, outcome.Calls, "Only the accepted write counts.");
        AssertEx.Equal<int?>(1, outcome.CoversToSequence, "The watermark stays at the last accepted write.");
        AssertEx.Equal(expected: 2, harness.Writes().Count, "The rejected write is attempted once and nothing follows it.");
    }

    [Test]
    public async Task DistillPendingAsync_WhenTheFirstCallReturnsNothing_PersistsNothing()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)));
        harness.Results.Enqueue(null);

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.DistillerReturnedNothing, outcome.Status);
        AssertEx.Empty(harness.Writes());
    }

    [Test]
    public async Task DistillPendingAsync_WhenALaterCallReturnsNothing_KeepsTheWatermarkAtTheLastPersistedCall()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)));
        harness.Results.Enqueue(Distillation("first fact", consumed: 2, coversTo: 1));
        harness.Results.Enqueue(null);

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.Distilled, outcome.Status);
        AssertEx.Equal<int?>(1, outcome.CoversToSequence);
        AssertEx.Equal(expected: 1, harness.Writes().Count);
    }

    [Test]
    public async Task DistillPendingAsync_TakesOnlyMessagesAfterTheWatermarkAndAtOrBelowTheBound()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 8)) with
        {
            ConversationState = ConversationStateSerializer.Serialize(new ConversationStateDocument()),
            ConversationStateCoversToSequence = 1
        });
        harness.Results.Enqueue(Distillation("fact", consumed: 3, coversTo: 4));

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: 4);

        AssertEx.Equal(ConversationStateDistillationStatus.Distilled, outcome.Status);
        AssertEx.Equal("2,3,4", Join(harness.InputSequences.Single()));
    }

    [Test]
    public async Task DistillPendingAsync_WhenTheStoredStateIsCorrupt_StartsFromAFreshDocument()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 2)) with
        {
            ConversationState = "{ not json"
        });
        harness.Results.Enqueue(Distillation("fact", consumed: 2, coversTo: 1));

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.Distilled, outcome.Status);
        AssertEx.Equal(expected: 0, harness.InputStateEntryCounts.Single());
        var written = AssertEx.NotNull(ConversationStateSerializer.Deserialize(harness.Writes().Single().State));
        AssertEx.Equal("e1", written.Entries.Single().Id);
    }

    [Test]
    public async Task DistillPendingAsync_WhenNoLocalModelIsInstalled_SkipsWithoutCallingTheDistiller()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)), localModel: null);

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.NoLocalModel, outcome.Status);
        AssertEx.Empty(harness.InputSequences);
        AssertEx.Empty(harness.Writes());
    }

    [Test]
    public async Task DistillPendingAsync_WhenDisabled_ReadsNothing()
    {
        var harness = new Harness(Conversation(CompletedMessages(count: 4)), options: new ConversationCompactionOptions
        {
            DistillEnabled = false
        });

        var outcome = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal(ConversationStateDistillationStatus.Disabled, outcome.Status);
        await harness.Persistence.DidNotReceiveWithAnyArgs().GetConversationAsync(Guid.Empty, default);
    }

    [Test]
    public async Task DistillPendingAsync_OrdersALateMintedSelectedSiblingByItsAnchor()
    {
        // Regenerating message-1 after later turns mints a sibling at sequence 4; its anchor stays 1.
        var variantGroupId = Guid.NewGuid();
        var messages = CompletedMessages(count: 4);
        messages[1] = messages[1] with
        {
            VariantGroupId = variantGroupId
        };
        var sibling = messages[1] with
        {
            MessageId = Guid.NewGuid(),
            Sequence = 4,
            Content = "sibling-answer",
            CreatedAtUtc = 4
        };
        messages.Add(sibling);
        var harness = new Harness(Conversation(messages) with
        {
            SelectedPath = new Dictionary<Guid, Guid>
            {
                [variantGroupId] = sibling.MessageId
            }
        });
        harness.Results.Enqueue(Distillation("fact", consumed: 4, coversTo: 3));

        _ = await harness.Service.DistillPendingAsync(ConversationId, requestedModel: null, upToAnchorSequence: null);

        AssertEx.Equal("0,1,2,3", Join(harness.InputSequences.Single()), "The chosen sibling is distilled at its anchor, not its physical sequence.");
        AssertEx.Equal("message-0,sibling-answer,message-2,message-3", Join(harness.InputContents.Single()));
    }

    private static string Join<T>(IEnumerable<T> values) =>
        string.Join(',', values);

    private static ConversationStateDistillation Distillation(string value, int consumed, int coversTo) =>
        new()
        {
            Delta = new ConversationStateDelta
            {
                Add =
                [
                    new ConversationStateProposedEntry
                    {
                        Category = ConversationStateCategory.Fact,
                        Value = value,
                        SourceSequences = [coversTo]
                    }
                ]
            },
            MessagesConsumed = consumed,
            CoversToSequence = coversTo
        };

    private static NodeChatConversationDto Conversation(IReadOnlyList<NodeChatPersistedMessageDto> messages) =>
        new()
        {
            ConversationId = ConversationId,
            Title = null,
            UserId = null,
            CreatedAtUtc = 0,
            LastSeenUtc = 0,
            Purged = false,
            Messages = messages
        };

    private static List<NodeChatPersistedMessageDto> CompletedMessages(int count) =>
        Enumerable.Range(0, count)
                  .Select(static sequence => new NodeChatPersistedMessageDto
                  {
                      MessageId = Guid.NewGuid(),
                      ConversationId = ConversationId,
                      RequestId = null,
                      Sequence = sequence,
                      Role = sequence % 2 == 0 ? "user" : "assistant",
                      Content = $"message-{sequence}",
                      Reasoning = null,
                      Status = NodeChatMessageStatusValues.Completed,
                      CreatedAtUtc = sequence,
                      UpdatedAtUtc = sequence,
                      Model = null,
                      Error = null,
                      MetadataJson = null
                  })
                  .ToList();

    /// <summary>The service over a substituted store and distiller; inputs are snapshotted because the loop reuses its list.</summary>
    private sealed class Harness
    {
        public Harness(NodeChatConversationDto conversation, string? localModel = "local-model", ConversationCompactionOptions? options = null)
        {
            Persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
            // A null write result means the guard rejected it (the row was restamped); the default accepts every write.
            Persistence.SetConversationStateAsync(Arg.Any<NodeChatSetConversationStateRequest>(), Arg.Any<CancellationToken>())
                       .Returns(_ => AcceptedWrites-- > 0 ? conversation : null);
            var distiller = Substitute.For<IConversationStateDistiller>();
            distiller.DistillAsync(Arg.Any<ConversationStateDistillerInput>(), Arg.Any<CancellationToken>())
                     .Returns(call =>
                     {
                         var input = call.Arg<ConversationStateDistillerInput>();
                         InputSequences.Add(input.Messages.Select(static message => message.Sequence).ToList());
                         InputContents.Add(input.Messages.Select(static message => message.Content).ToList());
                         InputStateEntryCounts.Add(input.State.Entries.Count);
                         return Results.Dequeue();
                     });
            var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
            resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(localModel);
            var capabilities = Substitute.For<IModelCapabilityResolver>();
            capabilities.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                        .Returns(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: false, IsCloud: false));
            var settings = Substitute.For<INodeSettingsStore>();
            settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());

            Service = new ConversationStateDistillationService(Persistence,
                distiller,
                resolver,
                capabilities,
                CreateWarmer(),
                settings,
                Options.Create(options ?? new ConversationCompactionOptions()),
                TimeProvider.System,
                NullLogger<ConversationStateDistillationService>.Instance);
        }

        public INodeChatPersistenceService Persistence { get; } = Substitute.For<INodeChatPersistenceService>();

        public ConversationStateDistillationService Service { get; }

        public Queue<ConversationStateDistillation?> Results { get; } = new();

        public int AcceptedWrites { get; set; } = int.MaxValue;

        public List<List<int>> InputSequences { get; } = [];

        public List<List<string>> InputContents { get; } = [];

        public List<int> InputStateEntryCounts { get; } = [];

        public List<NodeChatSetConversationStateRequest> Writes() =>
            Persistence.ReceivedCalls()
                       .Where(static call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.SetConversationStateAsync))
                       .Select(static call => (NodeChatSetConversationStateRequest)call.GetArguments()[0]!)
                       .ToList();

        // An Ollama-named provider: the warmer never reads a window from it, so the distiller keeps its ceiling.
        private static LocalRuntimeWarmer CreateWarmer()
        {
            var provider = Substitute.For<ILocalModelProvider>();
            provider.ProviderName.Returns("ollama");
            var providerResolver = Substitute.For<ILocalModelProviderResolver>();
            providerResolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
            return new LocalRuntimeWarmer(providerResolver, Substitute.For<IActiveCloudChatClientFactory>(), new FakeModelTrustResolver(), NullLogger<LocalRuntimeWarmer>.Instance,
                TimeProvider.System);
        }
    }
}
