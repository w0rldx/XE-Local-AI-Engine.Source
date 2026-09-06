namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConversationCompactionServiceTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();

    [Test]
    public async Task CompactAsync_WhenConversationNotFound_ReturnsConversationNotFoundAndNeverSummarizes()
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns((NodeChatConversationDto?)null);
        var summarizer = Substitute.For<IConversationSummarizer>();
        var service = CreateService(persistence, summarizer);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.ConversationNotFound, result.Outcome);
        await summarizer.DidNotReceive().SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenAtOrBelowKeepWindow_ReturnsNothingToCompactAndNeverPersists()
    {
        var messages = CompletedMessages(count: 8);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        var summarizer = Substitute.For<IConversationSummarizer>();
        var service = CreateService(persistence, summarizer);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.NothingToCompact, result.Outcome);
        await summarizer.DidNotReceive().SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>());
        await persistence.DidNotReceive().SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenTwelveMessagesNoPriorSummary_FoldsOldestFourAndPersistsSynopsis()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        AssertEx.Equal("SYNOPSIS", result.Summary);
        AssertEx.Equal(4, result.MessagesFolded);
        AssertEx.Equal(3, result.CoversToSequence);

        await persistence.Received(1)
                         .SetCompactionSummaryAsync(Arg.Is<NodeChatSetCompactionSummaryRequest>(request => request.Summary == "SYNOPSIS" && request.CoversToSequence == 3),
                             Arg.Any<CancellationToken>());

        await summarizer.Received(1)
                        .SummarizeAsync(Arg.Is<ConversationSummarizerInput>(input => input.PriorSummary == null && input.Messages.Count == 4), Arg.Any<CancellationToken>());
        var capturedInput = (ConversationSummarizerInput)summarizer.ReceivedCalls().Single().GetArguments()[0]!;
        AssertEx.Equal(4, capturedInput.Messages.Count);
        AssertEx.True(capturedInput.PriorSummary is null, "Expected no prior summary to be threaded through.");
        for (var sequence = 0; sequence < 4; sequence++)
        {
            AssertEx.Equal(messages[sequence].Content, capturedInput.Messages[sequence].Content);
            AssertEx.Equal(messages[sequence].Role, capturedInput.Messages[sequence].Role);
        }
    }

    [Test]
    public async Task CompactAsync_WhenNoLocalModelResolved_ReturnsNoLocalModelAndNeverSummarizes()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var summarizer = Substitute.For<IConversationSummarizer>();
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.NoLocalModel, result.Outcome);
        await summarizer.DidNotReceive().SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>());
        await persistence.DidNotReceive().SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenSummarizerReturnsNull_ReturnsSummarizerReturnedNothingAndNeverPersists()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.SummarizerReturnedNothing, result.Outcome);
        await persistence.DidNotReceive().SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenPriorCoverBelowCutoff_SummarizesOnlyTheNewSpanWithPriorSummary()
    {
        // 12 completed messages (0..11), keep=8 -> cutoff sequence is 3. A prior synopsis already covers sequence 1, so
        // only sequences (1, 3] = {2, 3} are new and should be folded, carrying the prior synopsis text forward.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages, compactionSummary: "OLD", coversToSequence: 1);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("MERGED");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        AssertEx.Equal(2, result.MessagesFolded);
        AssertEx.Equal(3, result.CoversToSequence);

        var capturedInput = (ConversationSummarizerInput)summarizer.ReceivedCalls().Single().GetArguments()[0]!;
        AssertEx.Equal("OLD", capturedInput.PriorSummary);
        AssertEx.Equal(2, capturedInput.Messages.Count);
        AssertEx.Equal(messages[2].Content, capturedInput.Messages[0].Content);
        AssertEx.Equal(messages[3].Content, capturedInput.Messages[1].Content);
    }

    [Test]
    public async Task CompactAsync_WhenPriorCoverAlreadyAtCutoff_ReturnsNothingToCompactAndNeverSummarizes()
    {
        // Prior synopsis already covers up to the computed cutoff sequence (3) -> nothing new is foldable.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages, compactionSummary: "OLD", coversToSequence: 3);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        var summarizer = Substitute.For<IConversationSummarizer>();
        var service = CreateService(persistence, summarizer);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.NothingToCompact, result.Outcome);
        AssertEx.Equal(0, result.MessagesFolded);
        await summarizer.DidNotReceive().SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>());
        await persistence.DidNotReceive().SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenSummaryExceedsMaxChars_TruncatesBeforePersisting()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("this synopsis is way longer than the configured cap");
        var options = Options.Create(new ConversationCompactionOptions
        {
            MaxSummaryChars = 5
        });
        var service = new ConversationCompactionService(persistence,
            summarizer,
            resolver,
            CreateCapabilityResolver(supportsThinking: false),
            CreateNodeSettingsStore(),
            options,
            TimeProvider.System,
            NullLogger<ConversationCompactionService>.Instance);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        AssertEx.Equal("this ", result.Summary);
        await persistence.Received(1).SetCompactionSummaryAsync(Arg.Is<NodeChatSetCompactionSummaryRequest>(request => request.Summary == "this "), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CompactAsync_WhenRequestedModelProvided_PrefersItOverTheNodeDefault()
    {
        // The user's selected model is fed to the resolver as the preference; the resolver honors it when it is a local
        // chat model (here the fake echoes it back) so summarization runs on the user's own model.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync("user-model", Arg.Any<CancellationToken>()).Returns("user-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId, "user-model");

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        await resolver.Received(1).ResolveAsync("user-model", Arg.Any<CancellationToken>());
        var capturedInput = (ConversationSummarizerInput)summarizer.ReceivedCalls().Single().GetArguments()[0]!;
        AssertEx.Equal("user-model", capturedInput.ModelName);
        AssertEx.Equal("user-model", result.ModelUsed);
        AssertEx.False(result.UsedFallbackModel, "The selected model was an installed local model, so no fallback occurred.");
    }

    [Test]
    public async Task CompactAsync_WhenSelectedModelNotLocal_SummarizesLocallyAndFlagsFallback()
    {
        // The user selected a cloud/unknown model; the resolver does not honor it and returns a node-local model instead.
        // Compaction still runs (on-device) but the result flags the fallback so the UI can tell the user.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync("cloud-model", Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId, "cloud-model");

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        AssertEx.True(result.UsedFallbackModel, "A non-honored selection must flag a fallback.");
        AssertEx.Equal("local-model", result.ModelUsed);
        var capturedInput = (ConversationSummarizerInput)summarizer.ReceivedCalls().Single().GetArguments()[0]!;
        AssertEx.Equal("local-model", capturedInput.ModelName);
    }

    [Test]
    public async Task CompactAsync_WhenNoRequestedModel_FallsBackToTheNodeDefault()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        await service.CompactAsync(ConversationId);

        // A blank request resolves against the node's persisted default (see CreateNodeSettingsStore).
        await resolver.Received(1).ResolveAsync("persisted-default", Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Regenerating an EARLY turn AFTER later turns exist mints a sibling whose PHYSICAL sequence lands past those
    ///     later turns. Ordering by that raw sequence makes the stale answer look like the conversation's NEWEST message:
    ///     it survives the keep-verbatim window while a genuinely recent exchange is folded away instead. Compaction must
    ///     order and fold in anchor space, and persist the anchor as the covered sequence so the send/regenerate paths
    ///     splice against the same value.
    /// </summary>
    [Test]
    public async Task CompactAsync_WhenALateMintedSiblingOfAnEarlyTurnIsSelected_FoldsByLogicalOrderAndCoversTheAnchor()
    {
        var variantGroupId = Guid.NewGuid();
        var messages = CompletedMessages(count: 12);
        // Turn message-1 into a variant group and regenerate it: the sibling takes the next free sequence (12).
        messages[1] = messages[1] with
        {
            VariantGroupId = variantGroupId
        };
        var lateSibling = messages[1] with
        {
            MessageId = Guid.NewGuid(),
            Sequence = 12,
            Content = "sibling-answer",
            CreatedAtUtc = 12,
            UpdatedAtUtc = 12
        };
        messages.Add(lateSibling);

        var conversation = Conversation(messages) with
        {
            SelectedPath = new Dictionary<Guid, Guid>
            {
                [variantGroupId] = lateSibling.MessageId
            }
        };
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        // The selected path has 12 entries and the keep window is 8, so the oldest 4 LOGICAL messages fold: the sibling
        // is the second-oldest turn, not the newest, and message-4 stays verbatim.
        AssertEx.Equal(expected: 4, result.MessagesFolded);
        AssertEx.Equal(expected: 3, result.CoversToSequence, "The covered sequence is the ANCHOR of the newest folded message, not a raw sequence.");
        await persistence.Received(1)
                         .SetCompactionSummaryAsync(Arg.Is<NodeChatSetCompactionSummaryRequest>(request => request.CoversToSequence == 3), Arg.Any<CancellationToken>());

        var capturedInput = (ConversationSummarizerInput)summarizer.ReceivedCalls().Single().GetArguments()[0]!;
        var foldedContents = capturedInput.Messages.Select(message => message.Content).ToArray();
        AssertEx.Equal(expected: 4, foldedContents.Length);
        AssertEx.Equal("message-0", foldedContents[0]);
        AssertEx.Equal("sibling-answer", foldedContents[1], "The late-minted sibling belongs to the SECOND turn and must fold there.");
        AssertEx.Equal("message-2", foldedContents[2]);
        AssertEx.Equal("message-3", foldedContents[3]);
    }

    [Test]
    public async Task CompactAsync_WhenAKeepWindowIsPassed_FoldsDownToItInsteadOfTheConfiguredWindow()
    {
        // The work-session step boundary passes 2: a session's state block is rebuilt from the database every step, so
        // only the previous exchange is worth keeping verbatim. The configured 8 would leave four whole steps unfolded.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId, requestedModel: null, recentMessagesToKeepVerbatim: 2);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        AssertEx.Equal(expected: 10, result.MessagesFolded, "A keep window of 2 leaves only the last exchange verbatim.");
        AssertEx.Equal(expected: 9, result.CoversToSequence);
    }

    [Test]
    public async Task CompactAsync_WhenAKeepWindowBelowTheFloorIsPassed_StillKeepsTheLastExchange()
    {
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId, requestedModel: null, recentMessagesToKeepVerbatim: 0);

        AssertEx.Equal(expected: 10, result.MessagesFolded, "The floor of 2 applies to the override exactly as it does to the configured value.");
    }

    [Test]
    public async Task CompactAsync_WhenNoKeepWindowIsPassed_LeavesTheOrdinaryChatPathOnTheConfiguredWindow()
    {
        // The three-argument member the chat compaction endpoint calls must keep behaving as it did: the default
        // interface implementation forwards a null override, and null means "the configured window".
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        IConversationCompactionService service = CreateService(persistence, summarizer, resolver);

        var result = await service.CompactAsync(ConversationId, "local-model", CancellationToken.None);

        AssertEx.Equal(expected: 4, result.MessagesFolded, "The configured keep window of 8 is unchanged for an ordinary chat.");
        AssertEx.Equal(expected: 3, result.CoversToSequence);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task CompactAsync_WhenTheModelSupportsThinking_PassesTheCapabilityToTheSummarizer(bool supportsThinking)
    {
        // The summarizer is a singleton and the capability resolver is scoped, so the capability has to be resolved HERE
        // and ride down on the input record. Resolved against the model the fold will actually run on, not the request.
        var messages = CompletedMessages(count: 12);
        var conversation = Conversation(messages);
        var persistence = Substitute.For<INodeChatPersistenceService>();
        persistence.GetConversationAsync(ConversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetCompactionSummaryAsync(Arg.Any<NodeChatSetCompactionSummaryRequest>(), Arg.Any<CancellationToken>()).Returns(conversation);
        var resolver = Substitute.For<ILocalDefaultChatModelResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("local-model");
        var summarizer = Substitute.For<IConversationSummarizer>();
        summarizer.SummarizeAsync(Arg.Any<ConversationSummarizerInput>(), Arg.Any<CancellationToken>()).Returns("SYNOPSIS");
        var capabilityResolver = CreateCapabilityResolver(supportsThinking);
        var service = CreateService(persistence, summarizer, resolver, capabilityResolver);

        var result = await service.CompactAsync(ConversationId);

        AssertEx.Equal(ConversationCompactionOutcome.Compacted, result.Outcome);
        await capabilityResolver.Received(1).ResolveAsync("local-model", Arg.Any<CancellationToken>());
        await summarizer.Received(1)
                        .SummarizeAsync(Arg.Is<ConversationSummarizerInput>(input => input.SupportsThinking == supportsThinking), Arg.Any<CancellationToken>());
    }

    private static ConversationCompactionService CreateService(INodeChatPersistenceService persistence,
        IConversationSummarizer summarizer,
        ILocalDefaultChatModelResolver? resolver = null,
        IModelCapabilityResolver? capabilityResolver = null)
    {
        resolver ??= Substitute.For<ILocalDefaultChatModelResolver>();
        return new ConversationCompactionService(persistence,
            summarizer,
            resolver,
            capabilityResolver ?? CreateCapabilityResolver(supportsThinking: false),
            CreateNodeSettingsStore(),
            Options.Create(new ConversationCompactionOptions()),
            TimeProvider.System,
            NullLogger<ConversationCompactionService>.Instance);
    }

    private static IModelCapabilityResolver CreateCapabilityResolver(bool supportsThinking)
    {
        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                          .Returns(new ModelCapabilitySnapshot(supportsThinking, SupportsTools: false, IsCloud: false));
        return capabilityResolver;
    }

    private static INodeSettingsStore CreateNodeSettingsStore()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
             .Returns(new StoredNodeSettings
             {
                 DefaultModelName = "persisted-default"
             });
        return store;
    }

    private static NodeChatConversationDto Conversation(IReadOnlyList<NodeChatPersistedMessageDto> messages, string? compactionSummary = null, int? coversToSequence = null) =>
        new(ConversationId,
            Title: null,
            UserId: null,
            CreatedAtUtc: 0,
            LastSeenUtc: 0,
            Purged: false,
            Messages: messages,
            CompactionSummary: compactionSummary,
            CompactionSummaryCoversToSequence: coversToSequence);

    private static List<NodeChatPersistedMessageDto> CompletedMessages(int count)
    {
        var messages = new List<NodeChatPersistedMessageDto>(count);
        for (var sequence = 0; sequence < count; sequence++)
        {
            var role = sequence % 2 == 0 ? "user" : "assistant";
            messages.Add(new NodeChatPersistedMessageDto(Guid.NewGuid(),
                ConversationId,
                RequestId: null,
                sequence,
                role,
                $"message-{sequence}",
                Reasoning: null,
                NodeChatMessageStatusValues.Completed,
                CreatedAtUtc: sequence,
                UpdatedAtUtc: sequence,
                Model: null,
                Error: null,
                MetadataJson: null));
        }

        return messages;
    }
}
