namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ConversationStateSourceMessageMapperTests
{
    [Test]
    public void Map_UsesTheAnchorSequenceRoleAndContent()
    {
        var mapped = ConversationStateSourceMessageMapper.Map(Message(parts: null), anchorSequence: 42);

        AssertEx.Equal(42, mapped.Sequence);
        AssertEx.Equal("assistant", mapped.Role);
        AssertEx.Equal("answer", mapped.Content);
        AssertEx.Empty(mapped.Tools);
    }

    [Test]
    public void Map_BuildsOneToolPartPerToolCallWithItsCollapsedResult()
    {
        var mapped = ConversationStateSourceMessageMapper.Map(Message(
        [
            new NodeChatMessagePart(NodeChatMessagePartKinds.Reasoning, 1, Text: "thinking"),
            new NodeChatMessagePart(NodeChatMessagePartKinds.Tool, 3, ToolCallId: "c2", Name: "read_file", State: NodeChatToolPartStates.Failed, Result: "not found"),
            new NodeChatMessagePart(NodeChatMessagePartKinds.Tool, 2, ToolCallId: "c1", Name: "search", State: NodeChatToolPartStates.Received, Args: """{"q":"x"}""", Result: "3 hits"),
            new NodeChatMessagePart(NodeChatMessagePartKinds.Text, 4, Text: "narration"),
            new NodeChatMessagePart(NodeChatMessagePartKinds.Notice, 5, Text: "notice", Name: "ModelSubstituted")
        ]), anchorSequence: 1);

        AssertEx.Equal(2, mapped.Tools.Count);
        AssertEx.Equal("search", mapped.Tools[0].Name);
        AssertEx.Equal("""{"q":"x"}""", mapped.Tools[0].ArgumentsExcerpt);
        AssertEx.Equal("3 hits", mapped.Tools[0].ResultExcerpt);
        AssertEx.Equal("read_file", mapped.Tools[1].Name);
        AssertEx.Null(mapped.Tools[1].ArgumentsExcerpt);
        AssertEx.Equal("not found", mapped.Tools[1].ResultExcerpt);
    }

    [Test]
    public void Map_CapsEachExcerptAtARuneBoundary()
    {
        var longArgs = "a" + string.Concat(Enumerable.Repeat("😀", 300));
        var longResult = new string('r', 1000);

        var tool = ConversationStateSourceMessageMapper.Map(Message(
        [
            new NodeChatMessagePart(NodeChatMessagePartKinds.Tool, 1, ToolCallId: "c1", Name: "search", Args: longArgs, Result: longResult)
        ]), anchorSequence: 1).Tools.Single();

        AssertEx.Equal(ConversationStateSourceMessageMapper.ToolExcerptChars, tool.ResultExcerpt?.Length ?? -1);
        var args = AssertEx.NotNull(tool.ArgumentsExcerpt);
        AssertEx.True(args.Length <= ConversationStateSourceMessageMapper.ToolExcerptChars);
        AssertEx.False(char.IsHighSurrogate(args[^1]), "The excerpt must not end inside a surrogate pair.");
        AssertEx.True(longArgs.StartsWith(args, StringComparison.Ordinal));
    }

    [Test]
    public void Map_SkipsAToolPartWithoutAName()
    {
        var mapped = ConversationStateSourceMessageMapper.Map(Message(
        [
            new NodeChatMessagePart(NodeChatMessagePartKinds.Tool, 1, ToolCallId: "c1", Name: null, Result: "orphan")
        ]), anchorSequence: 1);

        AssertEx.Empty(mapped.Tools);
    }

    private static NodeChatPersistedMessageDto Message(IReadOnlyList<NodeChatMessagePart>? parts) =>
        new()
        {
            MessageId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            RequestId = null,
            Sequence = 9,
            Role = "assistant",
            Content = "answer",
            Reasoning = null,
            Status = "completed",
            CreatedAtUtc = 0,
            UpdatedAtUtc = 0,
            Model = null,
            Error = null,
            MetadataJson = null,
            Parts = parts
        };
}
