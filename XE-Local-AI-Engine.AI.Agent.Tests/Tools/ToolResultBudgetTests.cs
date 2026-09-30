namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
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
        AssertEx.True(text.StartsWith(new string('a', 750), StringComparison.Ordinal));
        AssertEx.True(text.Contains("[truncated: 1000 of 3000 chars shown]", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Every registry's results reach the model through this one truncation (ClientLocal, custom and MCP tools all
    ///     wrap <c>BudgetedToolResultAIFunction</c>), so the head-and-tail shape holds for all of them.
    /// </summary>
    [Test]
    public void Truncate_KeepsTheHeadAndTheTailAroundAnExplicitMarker()
    {
        var text = string.Concat("HEAD", new string('m', 10_000), "TAIL-SUMMARY");

        var truncated = ToolResultBudget.Truncate(text, maxCharacters: 400);

        AssertEx.True(truncated.StartsWith("HEAD", StringComparison.Ordinal), "The head is kept.");
        AssertEx.True(truncated.EndsWith("TAIL-SUMMARY", StringComparison.Ordinal), "The tail is kept, so a closing line survives the cut.");
        AssertEx.Contains(truncated, $"\n\n[truncated: 400 of {text.Length} chars shown]\n\n");
        AssertEx.Equal(text[..300], truncated[..300], "Three quarters of the budget go to the head.");
        AssertEx.Equal(text[^100..], truncated[^100..], "One quarter goes to the tail.");
    }

    [Test]
    public void Truncate_NeverSplitsASurrogatePairAtTheHeadOrTheTailCut()
    {
        // The leading "a" puts a high surrogate at the head cut (index 299) and a low surrogate at the tail cut, so both cuts
        // would land inside an emoji; a lone half is invalid UTF-16 and a strict encoder refuses it.
        var text = string.Concat("a", string.Concat(Enumerable.Repeat("\U0001F600", 5_000)), "b");

        var truncated = ToolResultBudget.Truncate(text, maxCharacters: 400);

        _ = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(truncated);
        AssertEx.True(truncated.EndsWith("\U0001F600b", StringComparison.Ordinal), "the tail keeps whole characters");
    }

    [Test]
    public void Apply_TruncatedStringKeepsTheMediaOfTheOriginal()
    {
        var original = new string('i', 5_000) + "[image image/png, 1 KB]";
        var image = new DataContent(new byte[] { 1, 2, 3 }, "image/png");
        ToolResultMedia.Attach(original, [image]);

        var result = ToolResultBudget.Apply(original, maxCharacters: 100);

        AssertEx.True(ReferenceEquals(image, ToolResultMedia.Get(result).Single()), "An image block must survive its text being cut.");
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
