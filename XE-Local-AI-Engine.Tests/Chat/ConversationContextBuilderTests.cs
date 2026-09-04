namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The send-path history builder, now extracted so an integration execution replays a caller-managed session
///     exactly as chat replays a conversation. These pin the three properties the integration caller depends on: the
///     leading context takes slot 0, the compaction synopsis replaces the span it covers, and the turn's own user
///     message is last.
///     <para>
///         The "the extraction was verbatim" property is pinned by the SEND PATH's own existing context tests
///         (<c>NodeChatStreamServiceTests.SendMessageAsync_WhenCompacted_…</c> and the selected-path pair), which now
///         run THROUGH this class unchanged: a divergence in the move fails them.
///     </para>
/// </summary>
public sealed class ConversationContextBuilderTests
{
    [Test]
    public void Build_ForAnEmptyConversation_ReturnsOnlyTheSeed()
    {
        var conversationId = Guid.NewGuid();
        var seed = Message(conversationId, sequence: 0, "user", "the caller's input");

        var context = ConversationContextBuilder.Build(Conversation(conversationId, []), seed, selectedPath: null, attachmentContext: null);

        AssertEx.Equal(expected: 1, context.Count);
        AssertEx.Equal("the caller's input", context[0].Content);
        AssertEx.Equal(MessageRole.User, context[0].Role);
        AssertEx.Equal(expected: 0, context[0].SortOrder);
    }

    [Test]
    public void Build_WithOnlyFourArguments_LeavesImageAndKnowledgeContextNull()
    {
        // The two trailing defaults survived the extraction, which is the whole reason the integration caller can pass
        // four arguments: an integration execution has neither images nor a knowledge supplement.
        var conversationId = Guid.NewGuid();
        var attachment = new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = "PRIOR OUTPUTS",
            SortOrder = 0
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, [Message(conversationId, sequence: 0, "user", "turn one")]),
            Message(conversationId, sequence: 2, "user", "turn two"),
            selectedPath: null,
            attachment);

        AssertEx.Equal(expected: 3, context.Count);
        AssertEx.Equal("PRIOR OUTPUTS", context[0].Content);
        AssertEx.Equal(expected: 0, context[0].SortOrder);
        AssertEx.Equal("turn one", context[1].Content);
        AssertEx.Equal("turn two", context[2].Content);
    }

    [Test]
    public void Build_ForAContinuedConversation_OrdersHistoryThenTheSeed()
    {
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "first question"),
            Message(conversationId, sequence: 1, "assistant", "first answer")
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history),
            Message(conversationId, sequence: 2, "user", "second question"),
            selectedPath: null,
            attachmentContext: null);

        AssertEx.Equal(expected: 3, context.Count);
        AssertEx.Equal("first question", context[0].Content);
        AssertEx.Equal(MessageRole.Assistant, context[1].Role);
        AssertEx.Equal("first answer", context[1].Content);
        AssertEx.Equal("second question", context[2].Content);
    }

    [Test]
    public void Build_DropsIncompleteAndEmptyHistory()
    {
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "kept"),
            Message(conversationId, sequence: 1, "assistant", "failed turn", status: NodeChatMessageStatusValues.Failed),
            Message(conversationId, sequence: 2, "assistant", "   ")
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history),
            Message(conversationId, sequence: 3, "user", "now"),
            selectedPath: null,
            attachmentContext: null);

        AssertEx.Equal(expected: 2, context.Count);
        AssertEx.Equal("kept", context[0].Content);
        AssertEx.Equal("now", context[1].Content);
    }

    [Test]
    public void Build_WhenCompacted_SendsTheSynopsisInPlaceOfTheCoveredSpan()
    {
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "old question"),
            Message(conversationId, sequence: 1, "assistant", "old answer"),
            Message(conversationId, sequence: 2, "user", "recent question"),
            Message(conversationId, sequence: 3, "assistant", "recent answer")
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history, "SYNOPSIS", coversToSequence: 1),
            Message(conversationId, sequence: 4, "user", "the new turn"),
            selectedPath: null,
            attachmentContext: null);

        AssertEx.True(context[0].Content.Contains("SYNOPSIS", StringComparison.Ordinal), "The synopsis must lead the context.");
        AssertEx.Equal(expected: 0, context[0].SortOrder);
        AssertEx.False(context.Any(message => message.Content.Contains("old answer", StringComparison.Ordinal)),
            "Messages the synopsis covers must not be re-sent verbatim.");
        AssertEx.Contains(context.Select(message => message.Content), "recent question");
        AssertEx.Equal("the new turn", context[^1].Content);
    }

    [Test]
    public void Build_WithToolHistoryOn_KeepsAFailedToolBearingTurnTheSynopsisCouldNotHaveCovered()
    {
        // The compaction cutoff drops every row at or below it, and the summarizer only ever saw COMPLETED, non-blank
        // text — so a run that called save_artifact and then died had its one record of that action erased twice over:
        // once by the summarizer skipping it, once by the cutoff. The exemption is narrow on purpose: a sendable row
        // below the cutoff stays folded, because its text IS the synopsis.
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "save it"),
            Message(conversationId, sequence: 1, "assistant", "  ", status: NodeChatMessageStatusValues.Failed) with
            {
                Parts = [CompletedToolPart("call-1", "save_artifact")]
            },
            Message(conversationId, sequence: 2, "user", "recent question")
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history, "SYNOPSIS", coversToSequence: 1),
            Message(conversationId, sequence: 3, "user", "the new turn"),
            selectedPath: null,
            attachmentContext: null,
            imageContext: null,
            knowledgeContext: null,
            includeToolHistory: true);

        var replayed = AssertEx.NotNull(context.Single(message => message.Role == MessageRole.Assistant).ToolExchanges).Single();
        AssertEx.Equal("call-1", replayed.CallId, "The synopsis never saw the action, so the cutoff must not erase it.");
        AssertEx.False(context.Any(message => message.Content.Contains("save it", StringComparison.Ordinal)),
            "Everything else the synopsis covers stays folded.");
    }

    [Test]
    public void Build_WithToolHistoryOff_LetsTheCompactionCutoffDropAToolBearingTurn()
    {
        // The gate again, on the cutoff itself: chat's fold is byte-identical, parts or no parts.
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "save it"),
            Message(conversationId, sequence: 1, "assistant", "  ", status: NodeChatMessageStatusValues.Failed) with
            {
                Parts = [CompletedToolPart("call-1", "save_artifact")]
            },
            Message(conversationId, sequence: 2, "user", "recent question")
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history, "SYNOPSIS", coversToSequence: 1),
            Message(conversationId, sequence: 3, "user", "the new turn"),
            selectedPath: null,
            attachmentContext: null);

        AssertEx.False(context.Any(static message => message.Role == MessageRole.Assistant),
            "With the flag off the covered span is dropped whole, exactly as before.");
    }

    [Test]
    public void Build_WithToolHistoryOff_IsUnchangedByPersistedToolParts()
    {
        // The gate. Chat and every per-invocation run take the default, and for them a turn's persisted parts are a
        // render record and nothing else — including the Completed/blank filter, which must still drop the turn a
        // caller-managed continuation would keep.
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "user", "list the files"),
            Message(conversationId, sequence: 1, "assistant", "there are two files") with
            {
                Parts = [CompletedToolPart("call-1", "list_files")]
            },
            Message(conversationId, sequence: 2, "assistant", "  ", status: NodeChatMessageStatusValues.Failed) with
            {
                Parts = [CompletedToolPart("call-2", "save_artifact")]
            }
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history),
            Message(conversationId, sequence: 3, "user", "now"),
            selectedPath: null,
            attachmentContext: null);

        AssertEx.Equal(expected: 3, context.Count, "A failed, blank turn stays dropped with the flag off, parts or no parts.");
        AssertEx.True(context.All(static message => message.ToolExchanges is null));
    }

    [Test]
    public void Build_WithToolHistoryOn_ProjectsCompletedPartsInSequenceOrderAndKeepsTheTurnThatOnlyHasThem()
    {
        var conversationId = Guid.NewGuid();
        var history = new[]
        {
            Message(conversationId, sequence: 0, "assistant", "there are two files") with
            {
                Parts =
                [
                    CompletedToolPart("call-2", "read_file", sequence: 2, result: "second"),
                    CompletedToolPart("call-1", "list_files", sequence: 1, result: "first"),
                    RequestedToolPart("call-3", "write_file")
                ]
            },
            Message(conversationId, sequence: 1, "assistant", "   ", status: NodeChatMessageStatusValues.Cancelled) with
            {
                Parts = [CompletedToolPart("call-4", "save_artifact", result: "saved", isError: true)]
            }
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history),
            Message(conversationId, sequence: 2, "user", "now"),
            selectedPath: null,
            attachmentContext: null,
            imageContext: null,
            knowledgeContext: null,
            includeToolHistory: true);

        AssertEx.Equal(expected: 3, context.Count, "A cancelled, blank turn that completed a tool call is kept: the side effect is real.");

        var exchanges = AssertEx.NotNull(context[0].ToolExchanges);
        AssertEx.Equal(expected: 2, exchanges.Count, "A requested-only part has no result to pair with and is not replayed.");
        AssertEx.Equal("call-1", exchanges[0].CallId, "Exchanges follow the part SEQUENCE, not the persisted list order.");
        AssertEx.Equal("call-2", exchanges[1].CallId);

        var failed = AssertEx.NotNull(context[1].ToolExchanges).Single();
        AssertEx.Equal("call-4", failed.CallId);
        AssertEx.True(failed.IsError, "A failed tool result is replayed as one; the model acted on that text either way.");
    }

    [Test]
    public void Build_WithToolHistoryOn_ExcerptsAnOversizedResultToTheConfiguredCap()
    {
        var conversationId = Guid.NewGuid();
        var result = new string('r', count: 120);
        var history = new[]
        {
            Message(conversationId, sequence: 0, "assistant", "read it") with
            {
                Parts = [CompletedToolPart("call-1", "read_file", result: result)]
            }
        };

        var context = ConversationContextBuilder.Build(Conversation(conversationId, history),
            Message(conversationId, sequence: 1, "user", "now"),
            selectedPath: null,
            attachmentContext: null,
            imageContext: null,
            knowledgeContext: null,
            includeToolHistory: true,
            toolResultExcerptChars: 20);

        var replayed = AssertEx.NotNull(AssertEx.NotNull(context[0].ToolExchanges).Single().Result);
        AssertEx.True(replayed.StartsWith(new string('r', count: 20), StringComparison.Ordinal));
        AssertEx.Contains(replayed, "100 chars omitted");
    }

    [Test]
    public void Build_ExcerptCapDefault_MatchesTheBudgetersOwn()
    {
        // The parameter default is a copy of the options default so a static builder needs no DI. This is the pin that
        // the copy cannot drift: one truncation of a result must read the same wherever it happened.
        AssertEx.Equal(new ConversationContextBudgetOptions().HistoricalToolResultExcerptChars,
            ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars);
    }

    private static NodeChatMessagePart CompletedToolPart(string callId,
        string name,
        int sequence = 0,
        string? result = "ok",
        bool isError = false) =>
        new(NodeChatMessagePartKinds.Tool,
            sequence,
            Text: null,
            callId,
            name,
            isError ? NodeChatToolPartStates.Failed : NodeChatToolPartStates.Received,
            Args: "{}",
            result);

    private static NodeChatMessagePart RequestedToolPart(string callId, string name, int sequence = 3) =>
        new(NodeChatMessagePartKinds.Tool,
            sequence,
            Text: null,
            callId,
            name,
            NodeChatToolPartStates.Waiting,
            Args: "{}");

    private static NodeChatConversationDto Conversation(Guid conversationId,
        IReadOnlyList<NodeChatPersistedMessageDto> messages,
        string? compactionSummary = null,
        int? coversToSequence = null) =>
        new(conversationId,
            "integration session",
            UserId: null,
            CreatedAtUtc: 1,
            LastSeenUtc: 1,
            Purged: false,
            messages,
            CompactionSummary: compactionSummary,
            CompactionSummaryCoversToSequence: coversToSequence);

    private static NodeChatPersistedMessageDto Message(Guid conversationId,
        int sequence,
        string role,
        string content,
        string? status = null) =>
        new(Guid.NewGuid(),
            conversationId,
            RequestId: null,
            sequence,
            role,
            content,
            Reasoning: null,
            status ?? NodeChatMessageStatusValues.Completed,
            CreatedAtUtc: sequence + 1,
            UpdatedAtUtc: sequence + 1,
            Model: null,
            Error: null,
            MetadataJson: null);
}
