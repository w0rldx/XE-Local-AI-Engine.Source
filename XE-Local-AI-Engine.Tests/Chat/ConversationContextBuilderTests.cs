namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
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
