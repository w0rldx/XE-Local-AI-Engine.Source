namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The fold that hands the provider one leading system message (R4-06), graded on its own inputs.</summary>
[Category(TestCategories.Unit)]
public sealed class SingleSystemMessageChatClientTests
{
    [Test]
    public void Fold_WithAtMostOneSystemBlock_PassesTheInputsThrough()
    {
        IReadOnlyList<ChatMessage> messages = [new ChatMessage(ChatRole.System, "Persona."), new ChatMessage(ChatRole.User, "Hi.")];
        var options = new ChatOptions();

        var folded = SingleSystemMessageChatClient.Fold(messages, options);

        AssertEx.True(ReferenceEquals(messages, folded.Messages), "one system message needs no fold");
        AssertEx.True(ReferenceEquals(options, folded.Options), "options without instructions are not cloned");
    }

    [Test]
    public void Fold_TwoLeadingSystemMessagesWithoutInstructions_BecomeOne()
    {
        var folded = SingleSystemMessageChatClient.Fold([new ChatMessage(ChatRole.System, "Persona."), new ChatMessage(ChatRole.System, "Rules."), new ChatMessage(ChatRole.User, "Hi.")],
            options: null);

        AssertEx.Equal(expected: 2, folded.Messages.Count);
        AssertEx.Equal(ChatRole.System, folded.Messages[0].Role);
        AssertEx.Equal("Persona.\n\nRules.", folded.Messages[0].Text);
        AssertEx.Equal(ChatRole.User, folded.Messages[1].Role);
    }

    [Test]
    public void Fold_WithInstructions_PutsThemFirstAndNullsThemOnAClone()
    {
        var options = new ChatOptions
        {
            Instructions = "Skill listing."
        };

        var folded = SingleSystemMessageChatClient.Fold([new ChatMessage(ChatRole.System, "Persona."), new ChatMessage(ChatRole.User, "Hi.")], options);

        AssertEx.Equal("Skill listing.\n\nPersona.", folded.Messages[0].Text);
        AssertEx.Equal(expected: 1, folded.Messages.Count(static message => message.Role == ChatRole.System));
        AssertEx.True(AssertEx.NotNull(folded.Options).Instructions is null, "the folded instructions must not be sent a second time");
        AssertEx.Equal("Skill listing.", options.Instructions, "the caller's options must stay untouched");
    }

    [Test]
    public void Fold_WhenASystemMessageCarriesMoreThanText_LeavesTheInputsAsTheyAre()
    {
        var withProperties = new ChatMessage(ChatRole.System, "Persona.")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["cache_control"] = "ephemeral"
            }
        };
        IReadOnlyList<ChatMessage> messages = [withProperties, new ChatMessage(ChatRole.System, "Rules."), new ChatMessage(ChatRole.User, "Hi.")];

        var folded = SingleSystemMessageChatClient.Fold(messages, options: null);

        AssertEx.True(ReferenceEquals(messages, folded.Messages), "a fold would drop the message's properties");
    }
}
