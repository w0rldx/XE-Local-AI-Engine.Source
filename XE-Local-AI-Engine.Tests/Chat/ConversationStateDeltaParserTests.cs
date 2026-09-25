namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ConversationStateDeltaParserTests
{
    [Test]
    public void TryParse_ReadsTheFullShape()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse(
            """{"add":[{"category":"Decision","value":"Use Postgres","sourceSequences":[12,13],"supersedes":["e3"]}],"supersede":[{"entryId":"e5","bySequence":14}],"resolve":["e7"]}"""));

        var added = delta.Add.Single();
        AssertEx.Equal(ConversationStateCategory.Decision, added.Category);
        AssertEx.Equal("Use Postgres", added.Value);
        AssertEx.True(added.SourceSequences.SequenceEqual([12, 13]));
        AssertEx.Equal("e3", added.Supersedes.Single());
        var retired = delta.Supersede.Single();
        AssertEx.Equal("e5", retired.EntryId);
        AssertEx.Equal(14, retired.BySequence);
        AssertEx.Equal("e7", delta.Resolve.Single());
    }

    [Test]
    public void TryParse_AcceptsACodeFenceAndSurroundingProse()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse(
            "Here is the delta:\n```json\n{\"resolve\":[\"e1\"]}\n```\nDone."));

        AssertEx.Equal("e1", delta.Resolve.Single());
    }

    [Test]
    public void TryParse_MatchesPropertyAndCategoryNamesCaseInsensitively()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse(
            """{"ADD":[{"Category":"openquestion","VALUE":"Which port?"},{"category":"Open_Question","value":"Which host?"},{"category":"TOOL OUTCOME","value":"3 hits"}]}"""));

        AssertEx.True(delta.Add.Select(static entry => entry.Category)
                           .SequenceEqual([ConversationStateCategory.OpenQuestion, ConversationStateCategory.OpenQuestion, ConversationStateCategory.ToolOutcome]));
    }

    [Test]
    public void TryParse_TreatsMissingArraysAsEmpty()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse("""{"add":[{"category":"Fact","value":"x"}]}"""));

        AssertEx.Empty(delta.Supersede);
        AssertEx.Empty(delta.Resolve);
        AssertEx.Empty(delta.Add.Single().SourceSequences);
        AssertEx.Empty(delta.Add.Single().Supersedes);
    }

    [Test]
    public void TryParse_AnEmptyDeltaIsValid()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse("""{"add":[],"supersede":[],"resolve":[]}"""));

        AssertEx.True(delta.IsEmpty);
    }

    [Test]
    public void TryParse_DropsAnItemWithAnUnknownCategoryOrABlankValue()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse(
            """{"add":[{"category":"Mood","value":"happy"},{"category":"3","value":"numeric"},{"category":"Fact","value":"  "},{"category":"Fact"},{"category":"Fact","value":" kept "}]}"""));

        AssertEx.Equal("kept", delta.Add.Single().Value);
    }

    [Test]
    public void TryParse_DropsNonIntegerSequences()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse(
            """{"add":[{"category":"Fact","value":"x","sourceSequences":[1,"2",3.5,4]}],"supersede":[{"entryId":"e1","bySequence":"14"},{"entryId":"e2","bySequence":1.5},{"entryId":"e3"},{"entryId":" ","bySequence":2},{"entryId":"e4","bySequence":9}]}"""));

        AssertEx.True(delta.Add.Single().SourceSequences.SequenceEqual([1, 4]));
        var retired = delta.Supersede.Single();
        AssertEx.Equal("e4", retired.EntryId);
        AssertEx.Equal(9, retired.BySequence);
    }

    [Test]
    public void TryParse_ToleratesTrailingCommasAndDropsBlankIds()
    {
        var delta = AssertEx.NotNull(ConversationStateDeltaParser.TryParse("""{"resolve":["e1", "", 5, "e2",],}"""));

        AssertEx.True(delta.Resolve.SequenceEqual(["e1", "e2"]));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("no json here")]
    [Arguments("{ not valid json")]
    [Arguments("{\"add\": [}")]
    [Arguments("{\"memories\":[]}")]
    public void TryParse_ReturnsNullWhenNoDeltaObjectCanBeFound(string? output)
    {
        AssertEx.Null(ConversationStateDeltaParser.TryParse(output));
    }
}
