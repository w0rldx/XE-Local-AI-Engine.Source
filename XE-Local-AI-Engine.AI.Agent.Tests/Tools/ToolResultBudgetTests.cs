namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ToolResultBudgetTests
{
    [Test]
    public void Apply_StringWithinBudget_ReturnsUnchanged()
    {
        var result = ToolResultBudget.Apply("short", maxCharacters: 1024);

        AssertEx.Equal("short", result as string);
    }

    [Test]
    public void Apply_StringOverBudget_TruncatesWithMarker()
    {
        var result = ToolResultBudget.Apply(new string('a', 3_000), maxCharacters: 1_000);

        var text = result as string ?? throw new AssertionException("Expected a string.");
        AssertEx.True(text.StartsWith(new string('a', 1_000), StringComparison.Ordinal));
        AssertEx.True(text.Contains("[truncated: 1000 of 3000 chars shown]", StringComparison.Ordinal));
    }

    [Test]
    public void Apply_Null_ReturnsNull()
    {
        AssertEx.True(ToolResultBudget.Apply(result: null, maxCharacters: 1024) is null);
    }

    [Test]
    public void Apply_TextContentOverBudget_ReturnsTruncatedTextContent()
    {
        var content = new TextContent(new string('b', 2_000));

        var result = ToolResultBudget.Apply(content, maxCharacters: 500);

        var textContent = result as TextContent ?? throw new AssertionException("Expected a TextContent.");
        AssertEx.True(textContent.Text.Contains("[truncated: 500 of 2000 chars shown]", StringComparison.Ordinal));
    }

    [Test]
    public void Apply_TextContentWithinBudget_ReturnsSameInstance()
    {
        var content = new TextContent("fits");

        var result = ToolResultBudget.Apply(content, maxCharacters: 1024);

        AssertEx.True(ReferenceEquals(content, result), "a within-budget content must not be re-allocated");
    }

    [Test]
    public void Apply_JsonElementOverBudget_TruncatesToString()
    {
        using var document = JsonDocument.Parse($$"""{"data":"{{new string('c', 3_000)}}"}""");
        var element = document.RootElement.Clone();

        var result = ToolResultBudget.Apply(element, maxCharacters: 800);

        var text = result as string ?? throw new AssertionException("Expected a truncated string.");
        AssertEx.True(text.Contains("[truncated:", StringComparison.Ordinal));
    }

    [Test]
    public void Apply_ContentArrayOverBudget_CollapsesTextAndKeepsNonText()
    {
        var nonText = new AIContent();
        AIContent[] parts = [new TextContent(new string('d', 2_000)), nonText, new TextContent(new string('e', 2_000))];

        var result = ToolResultBudget.Apply(parts, maxCharacters: 1_000);

        var rebuilt = result as AIContent[] ?? throw new AssertionException("Expected an AIContent array.");
        AssertEx.Equal(expected: 2, rebuilt.Length);
        var text = rebuilt[0] as TextContent ?? throw new AssertionException("Expected the collapsed text block first.");
        AssertEx.True(text.Text.Contains("[truncated: 1000 of 4000 chars shown]", StringComparison.Ordinal));
        AssertEx.True(ReferenceEquals(nonText, rebuilt[1]), "the non-text block must be preserved");
    }

    [Test]
    public void Apply_ContentArrayWithinBudget_ReturnsUnchanged()
    {
        AIContent[] parts = [new TextContent("a"), new TextContent("b")];

        var result = ToolResultBudget.Apply(parts, maxCharacters: 1024);

        AssertEx.True(ReferenceEquals(parts, result), "a within-budget array must pass through untouched");
    }
}
