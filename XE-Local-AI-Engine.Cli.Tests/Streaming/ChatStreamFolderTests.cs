namespace XE_Local_AI_Engine.Cli.Tests.Streaming;

using XE_Local_AI_Engine.Cli.Streaming;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ChatStreamFolderTests
{
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly Guid MessageId = Guid.NewGuid();
    private static readonly Guid InvocationId = Guid.NewGuid();

    [Test]
    public void Fold_WhenDeltasArriveInOrder_AppendsAndSurfacesEachDelta()
    {
        var folder = NewFolder();

        var first = folder.Fold(Delta("Hel", 0, reasoning: "th", reasoningOffset: 0));
        var second = folder.Fold(Delta("lo", 3, reasoning: "ink", reasoningOffset: 2));

        var firstEvent = AssertEx.NotNull(first.Event);
        AssertEx.Equal(CliStreamEventKind.Delta, firstEvent.Kind);
        AssertEx.Equal("Hel", firstEvent.Text);
        AssertEx.Equal("th", firstEvent.Reasoning);
        AssertEx.Equal("lo", AssertEx.NotNull(second.Event).Text);
        AssertEx.Equal("Hello", folder.Content);
        AssertEx.Equal("think", folder.Reasoning);
        AssertEx.False(first.NeedsResume || second.NeedsResume);
    }

    [Test]
    [Arguments("content gap", 10L, 0L)]
    [Arguments("reasoning gap", 3L, 4L)]
    [Arguments("content partly behind", 1L, 0L)]
    public void Fold_WhenADeltaDoesNotContinueTheText_AsksForAResumeAndKeepsTheText(string row, long contentOffset, long reasoningOffset)
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("Hel", 0));

        var outcome = folder.Fold(Delta("lo!", contentOffset, reasoning: "x", reasoningOffset: reasoningOffset));

        AssertEx.True(outcome.NeedsResume, row);
        AssertEx.Null(outcome.Event, row);
        AssertEx.Equal("Hel", folder.Content, row);
    }

    [Test]
    public void Fold_WhenADeltaIsWhollyBehindTheText_DropsItAsAReplay()
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("Hello", 0));

        var outcome = folder.Fold(Delta("ell", 1));

        AssertEx.False(outcome.NeedsResume);
        AssertEx.Null(outcome.Event);
        AssertEx.Equal("Hello", folder.Content);
    }

    [Test]
    public void Fold_WhenASnapshotArrives_ReplacesTheTextAndRebasesTheOffsets()
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("Hel", 0));

        var snapshot = folder.Fold(Frame(ChatStreamEventTypes.AssistantSnapshot, content: "Hello wor", reasoning: "r"));
        var next = folder.Fold(Delta("ld", 9, reasoning: string.Empty, reasoningOffset: 1));

        var snapshotEvent = AssertEx.NotNull(snapshot.Event);
        AssertEx.Equal(CliStreamEventKind.Snapshot, snapshotEvent.Kind);
        AssertEx.Equal("Hello wor", snapshotEvent.Text);
        AssertEx.Equal("r", snapshotEvent.Reasoning);
        AssertEx.False(next.NeedsResume);
        AssertEx.Equal("Hello world", folder.Content);
    }

    [Test]
    public void Fold_WhenTheServerAsksToReconcile_AsksForAResumeAndSurfacesNothing()
    {
        var folder = NewFolder();

        var outcome = folder.Fold(Frame(ChatStreamEventTypes.AssistantReconcile));

        AssertEx.True(outcome.NeedsResume);
        AssertEx.Null(outcome.Event);
    }

    [Test]
    [Arguments(ChatStreamEventTypes.AssistantCompleted, nameof(CliStreamEventKind.Completed))]
    [Arguments(ChatStreamEventTypes.AssistantCancelled, nameof(CliStreamEventKind.Cancelled))]
    [Arguments(ChatStreamEventTypes.AssistantFailed, nameof(CliStreamEventKind.Failed))]
    [Arguments(ChatStreamEventTypes.AssistantInterrupted, nameof(CliStreamEventKind.Interrupted))]
    public void Fold_WhenATerminalArrives_SurfacesTheFullTextAndDropsEverythingAfter(string type, string expected)
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("Hel", 0));

        var terminal = folder.Fold(Frame(type, content: "Hello"));
        var late = folder.Fold(Delta("!", 5));

        var terminalEvent = AssertEx.NotNull(terminal.Event);
        AssertEx.Equal(expected, terminalEvent.Kind.ToString());
        AssertEx.Equal("Hello", terminalEvent.Text);
        AssertEx.True(folder.IsTerminal);
        AssertEx.Null(late.Event);
        AssertEx.False(late.NeedsResume);
    }

    [Test]
    public void Fold_WhenATerminalCarriesNoText_KeepsTheFoldedText()
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("partial", 0));

        var terminal = folder.Fold(Frame(ChatStreamEventTypes.AssistantCancelled));

        AssertEx.Equal("partial", AssertEx.NotNull(terminal.Event).Text);
    }

    [Test]
    [Arguments("content-only terminal keeps reasoning", "Hello", null, "Hello", "think")]
    [Arguments("reasoning-only terminal keeps content", null, "thought", "Hel", "thought")]
    public void Fold_WhenATerminalCarriesOnlyOneField_KeepsTheFoldedTextOfTheOther(string row, string? content, string? reasoning, string expectedText, string expectedReasoning)
    {
        var folder = NewFolder();
        _ = folder.Fold(Delta("Hel", 0, reasoning: "think", reasoningOffset: 0));

        var terminal = AssertEx.NotNull(folder.Fold(Frame(ChatStreamEventTypes.AssistantCompleted, content: content, reasoning: reasoning)).Event);

        AssertEx.Equal(expectedText, terminal.Text, row);
        AssertEx.Equal(expectedReasoning, terminal.Reasoning, row);
    }

    [Test]
    public void Fold_WhenAResumeReplaysAPrompt_SurfacesItOnce()
    {
        var folder = NewFolder();
        var approval = Frame(ChatStreamEventTypes.ApprovalRequested, approvalRequestId: "approval-1");
        var question = Frame(ChatStreamEventTypes.QuestionRequested, questionRequestId: "question-1");

        var outcomes = new[] { approval, question, approval, question }.Select(folder.Fold).ToList();

        AssertEx.Equal(CliStreamEventKind.ApprovalRequested, AssertEx.NotNull(outcomes[0].Event).Kind);
        AssertEx.Equal(CliStreamEventKind.QuestionRequested, AssertEx.NotNull(outcomes[1].Event).Kind);
        AssertEx.Null(outcomes[2].Event);
        AssertEx.Null(outcomes[3].Event);
    }

    [Test]
    public void Fold_WhenAResumedFrameCarriesTheInvocationIdAsItsMessageId_MapsItBackToTheAssistantMessage()
    {
        var folder = NewFolder();

        var outcome = folder.Fold(Frame(ChatStreamEventTypes.AssistantSnapshot, content: "x", messageId: InvocationId));

        var surfaced = AssertEx.NotNull(outcome.Event);
        AssertEx.Equal(MessageId, surfaced.MessageId);
        AssertEx.Equal(InvocationId, surfaced.RequestId);
        AssertEx.Equal(ConversationId, surfaced.ConversationId);
    }

    [Test]
    [Arguments(ChatStreamEventTypes.UserMessagePersisted, nameof(CliStreamEventKind.Status))]
    [Arguments(ChatStreamEventTypes.AssistantPending, nameof(CliStreamEventKind.Status))]
    [Arguments(ChatStreamEventTypes.AssistantQueued, nameof(CliStreamEventKind.Status))]
    [Arguments(ChatStreamEventTypes.AssistantStreaming, nameof(CliStreamEventKind.Status))]
    [Arguments(ChatStreamEventTypes.AssistantPhase, nameof(CliStreamEventKind.Status))]
    [Arguments(ChatStreamEventTypes.ToolCallRequested, nameof(CliStreamEventKind.ToolActivity))]
    [Arguments(ChatStreamEventTypes.ToolCallCompleted, nameof(CliStreamEventKind.ToolActivity))]
    [Arguments(ChatStreamEventTypes.AssistantNotice, nameof(CliStreamEventKind.Notice))]
    [Arguments("a-type-a-newer-node-adds", nameof(CliStreamEventKind.Status))]
    public void Fold_MapsEachProgressTypeToItsKind(string type, string expected)
    {
        var outcome = NewFolder().Fold(Frame(type));

        AssertEx.Equal(expected, AssertEx.NotNull(outcome.Event).Kind.ToString());
    }

    private static ChatStreamFolder NewFolder() =>
        new(ConversationId, MessageId, InvocationId);

    private static ChatStreamEvent Delta(string text, long offset, string reasoning = "", long reasoningOffset = 0) =>
        new()
        {
            Type = ChatStreamEventTypes.AssistantDelta,
            ConversationId = ConversationId,
            MessageId = MessageId,
            RequestId = InvocationId,
            Status = "streaming",
            Delta = text,
            ContentOffset = offset,
            ReasoningDelta = reasoning,
            ReasoningOffset = reasoningOffset
        };

    private static ChatStreamEvent Frame(string type,
        string? content = null,
        string? reasoning = null,
        string? approvalRequestId = null,
        string? questionRequestId = null,
        Guid? messageId = null) =>
        new()
        {
            Type = type,
            ConversationId = ConversationId,
            MessageId = messageId ?? MessageId,
            RequestId = InvocationId,
            Status = "streaming",
            Content = content,
            Reasoning = reasoning,
            ApprovalRequestId = approvalRequestId,
            QuestionRequestId = questionRequestId
        };
}
