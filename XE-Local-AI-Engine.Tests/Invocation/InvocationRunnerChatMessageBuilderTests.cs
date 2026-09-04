namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The message list a package is rendered into, in isolation from the runner that sends it. The shape matters at
///     the wire: Microsoft.Extensions.AI's OpenAI client takes its tool-calls-only branch only when the assistant
///     message carries no content part, and a <c>tool</c> message not preceded by its call is what a strict chat
///     template rejects.
/// </summary>
public sealed class InvocationRunnerChatMessageBuilderTests
{
    [Test]
    public void BuildChatMessages_ForAToolExchange_EmitsACallOnlyAssistantMessageThenItsToolResult()
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("list the files")
                                           .WithToolExchangeMessage("there are two files",
                                               sortOrder: 1,
                                               new ConversationToolExchange("call-1", "list_files", "{\"path\":\".\"}", "a.txt", IsError: false))
                                           .Build();

        var messages = InvocationRunner.BuildChatMessages(package);

        AssertEx.Equal(expected: 4, messages.Count);
        AssertEx.Equal(ChatRole.User, messages[0].Role);

        AssertEx.Equal(ChatRole.Assistant, messages[1].Role);
        AssertEx.Equal(expected: 1, messages[1].Contents.Count, "A call-only assistant message must carry no text part.");
        var call = messages[1].Contents.OfType<FunctionCallContent>().Single();
        AssertEx.Equal("call-1", call.CallId);
        AssertEx.Equal("list_files", call.Name);
        AssertEx.Equal(".", AssertEx.NotNull(call.Arguments)["path"]?.ToString(), "The recorded argument JSON must reach the model parsed.");

        AssertEx.Equal(ChatRole.Tool, messages[2].Role);
        var result = messages[2].Contents.OfType<FunctionResultContent>().Single();
        AssertEx.Equal("call-1", result.CallId);
        AssertEx.Equal("a.txt", result.Result?.ToString(), "A string result rides UNWRAPPED; re-serializing it would reach the model quoted.");

        AssertEx.Equal(ChatRole.Assistant, messages[3].Role);
        AssertEx.Equal("there are two files", messages[3].Contents.OfType<TextContent>().Single().Text);
    }

    [Test]
    public void BuildChatMessages_WhenTheTurnHasNoTextOfItsOwn_EmitsNoTrailingMessage()
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("save it")
                                           .WithToolExchangeMessage(string.Empty,
                                               sortOrder: 1,
                                               new ConversationToolExchange("call-1", "save_artifact", "{}", "saved", IsError: false))
                                           .Build();

        var messages = InvocationRunner.BuildChatMessages(package);

        AssertEx.Equal(expected: 3, messages.Count, "An empty trailing assistant message is content, not absence: it must not be emitted.");
        AssertEx.Equal(ChatRole.Tool, messages[^1].Role);
    }

    [Test]
    public void BuildChatMessages_WhenTheRecordedArgumentsAreNotJson_YieldsNullArgumentsRatherThanThrowing()
    {
        // A historical record must never be able to fault a live turn. The call still reaches the model — with no
        // arguments — because the fact that it happened is the load-bearing half.
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("go")
                                           .WithToolExchangeMessage("done",
                                               sortOrder: 1,
                                               new ConversationToolExchange("call-1", "list_files", "not json at all", "a.txt", IsError: false))
                                           .Build();

        var messages = InvocationRunner.BuildChatMessages(package);

        var call = messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Single();
        AssertEx.Null(call.Arguments);
        AssertEx.Null(call.Exception, "A parse failure must not stamp the content with a live exception.");
    }

    [Test]
    public void BuildChatMessages_WhenACallRecordedNoArguments_EmitsTheCallWithNone()
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("go")
                                           .WithToolExchangeMessage("done",
                                               sortOrder: 1,
                                               new ConversationToolExchange("call-1", "get_current_time", ArgumentsJson: null, "12:00", IsError: false))
                                           .Build();

        var messages = InvocationRunner.BuildChatMessages(package);

        var call = messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Single();
        AssertEx.Equal("get_current_time", call.Name);
        AssertEx.Null(call.Arguments);
    }

    [Test]
    public void BuildChatMessages_WithoutToolExchanges_IsUnchanged()
    {
        var package = RuntimePackageBuilder.Valid()
                                           .WithUserMessage("late")
                                           .WithConversationMessage(MessageRole.Assistant, "middle", sortOrder: 1)
                                           .WithConversationMessage(MessageRole.User, "early", sortOrder: -1)
                                           .Build();

        var messages = InvocationRunner.BuildChatMessages(package);

        AssertEx.Equal(expected: 3, messages.Count);
        AssertEx.Equal("early", messages[0].Text);
        AssertEx.Equal("late", messages[1].Text);
        AssertEx.Equal("middle", messages[2].Text);
        AssertEx.True(messages.All(static message => !message.Contents.OfType<FunctionCallContent>().Any()));
    }
}
