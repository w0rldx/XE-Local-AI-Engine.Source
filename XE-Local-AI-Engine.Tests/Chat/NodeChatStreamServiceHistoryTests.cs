namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;
using static NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceHistoryTests
{
    [Test]
    public async Task SendMessageAsync_WhenOlderVariantSelected_SendsSelectedVariantNotNewest()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();
        // Explicitly select the OLDER variant; the resolver would otherwise default to the newest sibling.
        var selectedPath = new Dictionary<Guid, Guid>
        {
            [variantGroupId] = olderVariantId
        };

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            selectedPath,
            requestSelection: null);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "older answer");
        AssertEx.False(assistantContents.Contains("newer answer"), "The newest variant must be excluded when an older variant is selected.");
    }

    [Test]
    public async Task SendMessageAsync_WhenNoSelection_FallsBackToNewestVariant()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection: null,
            requestSelection: null);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "newer answer");
        AssertEx.False(assistantContents.Contains("older answer"), "With no selection the newest variant is the default.");
    }

    [Test]
    public async Task SendMessageAsync_WhenRequestCarriesSelection_PersistsAndUsesIt()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();
        // No persisted selection; the request rides a selection for the OLDER variant. The service must persist it
        // (SetSelectedPathAsync) and use it to build context.
        var requestSelection = new Dictionary<Guid, Guid>
        {
            [variantGroupId] = olderVariantId
        };

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection: null,
            requestSelection);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "older answer");
        AssertEx.True(runner.SelectionPersisted, "A request-supplied selection must be persisted via SetSelectedPathAsync.");
    }

    [Test]
    public async Task SendMessageAsync_WhenCompacted_SendsTheSynopsisInPlaceOfTheCoveredHistory()
    {
        // Pins the send path's compaction splice, which now runs through CompactionContextResolver — the same helper
        // the regenerate path uses, so a regression here would silently desync both context builders.
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            persistedSelection: null,
            requestSelection: null,
            compactionSummary: "SYNOPSIS",
            compactionCoversToSequence: 2);

        // ONE synthetic synopsis leads the context, followed only by the new user turn (sequence 3) the synopsis
        // does not cover.
        AssertEx.Equal(expected: 2, runner.CapturedContext.Count);
        AssertEx.True(runner.CapturedContext[0].Content.Contains("SYNOPSIS", StringComparison.Ordinal), "The synopsis must lead the context.");
        AssertEx.Equal(MessageRole.User, runner.CapturedContext[0].Role);
        AssertEx.Equal("follow up", runner.CapturedContext[1].Content);
        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains("answer", StringComparison.Ordinal)),
            "Messages the synopsis covers must not be re-sent verbatim.");
    }

    [Test]
    public async Task SendMessageAsync_WithASelectionSwitch_DoesNotSendTheSynopsisTheSwitchCleared()
    {
        // Persisting the request's selection clears the synopsis, so a conversation read BEFORE that write would build
        // the turn from a synopsis the database no longer has, over history the turn-scoped read skipped.
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            Guid.NewGuid(),
            persistedSelection: null,
            requestSelection: new Dictionary<Guid, Guid>
            {
                [variantGroupId] = olderVariantId
            },
            compactionSummary: "SYNOPSIS",
            compactionCoversToSequence: 2);

        AssertEx.False(runner.CapturedContext.Any(message => message.Content.Contains("SYNOPSIS", StringComparison.Ordinal)),
            "The selection switch cleared the synopsis, so the turn must not send it.");
        AssertEx.Contains(runner.CapturedContext.Select(message => message.Content).ToList(), "original question");
        AssertEx.Contains(runner.CapturedContext.Select(message => message.Content).ToList(), "older answer");
    }

    /// <summary>
    ///     A sibling regenerated for an EARLY turn has a PHYSICAL sequence past the later turns. Sent selected, it still
    ///     sits at its early turn; ordering by raw sequence would break user/assistant alternation.
    /// </summary>
    [Test]
    public async Task SendMessageAsync_WhenALateMintedSiblingOfAnEarlyTurnIsSelected_SendsItAtTheEarlyPosition()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var originalAnswerId = Guid.NewGuid();
        var lateSiblingId = Guid.NewGuid();

        // U1(1) A1(2,G) U2(3) A2(4) U3(5) A3(6), then regenerate A1 -> A1'(7,G) and select it.
        IReadOnlyList<NodeChatPersistedMessageDto> messages =
        [
            AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 1, "user", "u-one"),
            AnchorScenarioMessage(conversationId, originalAnswerId, sequence: 2, "assistant", "a-one", variantGroupId),
            AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 3, "user", "u-two"),
            AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 4, "assistant", "a-two"),
            AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 5, "user", "u-three"),
            AnchorScenarioMessage(conversationId, Guid.NewGuid(), sequence: 6, "assistant", "a-three"),
            AnchorScenarioMessage(conversationId, lateSiblingId, sequence: 7, "assistant", "a-one-prime", variantGroupId)
        ];

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            originalAnswerId,
            lateSiblingId,
            new Dictionary<Guid, Guid>
            {
                [variantGroupId] = lateSiblingId
            },
            requestSelection: null,
            conversationMessages: messages);

        var contents = runner.CapturedContext.Select(message => message.Content).ToArray();
        AssertEx.Equal(expected: 7, contents.Length);
        AssertEx.Equal("u-one", contents[0]);
        AssertEx.Equal("a-one-prime", contents[1]);
        AssertEx.Equal("u-two", contents[2]);
        AssertEx.Equal("a-two", contents[3]);
        AssertEx.Equal("u-three", contents[4]);
        AssertEx.Equal("a-three", contents[5]);
        AssertEx.Equal("follow up", contents[6]);
        AssertEx.False(contents.Contains("a-one"), "The deselected original sibling must stay out of context.");
    }

    [Test]
    public async Task SendMessageAsync_FollowUpTurn_ReplaysTheEarlierToolExchangeAsACappedExcerpt()
    {
        var conversationId = Guid.NewGuid();
        var runner = await RunWithVariantConversationAsync(conversationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            persistedSelection: null,
            requestSelection: null,
            conversationMessages: ToolInvestigationTurn(conversationId));

        var assistant = runner.CapturedContext.Single(message => message.Role == MessageRole.Assistant);
        var exchange = AssertEx.NotNull(assistant.ToolExchanges, "The follow-up must see what the earlier tool call returned.").Single();
        AssertEx.Equal("call-1", exchange.CallId);
        AssertEx.Equal("mcp__codegraph__explore", exchange.Name);
        var result = AssertEx.NotNull(exchange.Result);
        AssertEx.True(result.StartsWith("Classify maps HttpRequestException", StringComparison.Ordinal), "The excerpt keeps the head of the result.");
        AssertEx.Contains(result, "[truncated:");
        AssertEx.True(result.Length < 2_100, $"The replay is capped at the historical excerpt, got {result.Length} chars.");
        AssertEx.False(runner.Events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.ToolHistoryWithheld)));
    }

    [Test]
    public async Task SendMessageAsync_FollowUpTurnOnACloudModel_WithholdsTheToolHistoryAndNotifies()
    {
        var conversationId = Guid.NewGuid();
        var runner = await RunWithVariantConversationAsync(conversationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            persistedSelection: null,
            requestSelection: null,
            conversationMessages: ToolInvestigationTurn(conversationId),
            model: "gpt-5.6-sol");

        AssertEx.True(runner.CapturedContext.All(message => message.ToolExchanges is null), "Node-local tool output must not reach a cloud model without the opt-in.");
        AssertEx.ContainsSingle(runner.Events, streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.ToolHistoryWithheld));
        AssertEx.Contains(runner.Events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.ToolHistoryWithheld)).NoticeMessage,
            "grant local data on the model's external connection");
    }
}
