namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Residue think tags at the head of a llama-server answer (a stray close tag, the empty pair a zero budget forces
///     on LFM2.5) never reach the visible content, however they are chunked. Shapes captured at b10201.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LeadingThinkTagStripperTests
{
    [Test]
    // LFM2.5 with reasoning_budget_tokens 0, streamed token by token.
    [Arguments(new[] { "\n", "<think>", "</think>", "\n", "Sure", "!" }, "Sure!")]
    // Two empty pairs in a row (LFM2.5, "What is 17 + 25?").
    [Arguments(new[] { "\n<think></think>\n<think></think>\nTo determine" }, "To determine")]
    // A stray close after a budget-forced end, split mid-tag.
    [Arguments(new[] { "</th", "ink>", "\n\nHello", " there" }, "Hello there")]
    // Plain answers are untouched apart from leading blank lines; the indentation of a real first line stays.
    [Arguments(new[] { "\n", "Hello" }, "Hello")]
    [Arguments(new[] { "    ", "return x;\n}" }, "    return x;\n}")]
    [Arguments(new[] { "\n\n", "  ", "- item" }, "  - item")]
    // Whitespace that belongs to the stripped tag goes with it; the indentation on the next line does not.
    [Arguments(new[] { "<think></think>\n", "    code" }, "    code")]
    [Arguments(new[] { "</think>  Hello" }, "Hello")]
    [Arguments(new[] { "<", "b>bold</b>" }, "<b>bold</b>")]
    // An open tag followed by text is not residue: the parser missed it, so it stays visible rather than vanish.
    [Arguments(new[] { "<think>", "plan", "</think>answer" }, "<think>plan</think>answer")]
    // A tag later in the answer is left alone.
    [Arguments(new[] { "Answer ", "</think> tail" }, "Answer </think> tail")]
    public void Push_StripsOnlyResidueAtTheHead(string[] chunks, string expected)
    {
        var stripper = new LeadingThinkTagStripper();

        var emitted = string.Concat(chunks.Select(stripper.Push)) + stripper.Flush();

        AssertEx.Equal(expected, emitted);
    }

    [Test]
    public void Flush_ReleasesAHeldPartialTagButDropsBareResidue()
    {
        var partial = new LeadingThinkTagStripper();
        AssertEx.Equal(string.Empty, partial.Push("<thi"));
        AssertEx.Equal("<thi", partial.Flush());

        var residue = new LeadingThinkTagStripper();
        AssertEx.Equal(string.Empty, residue.Push("\n<think></think>\n"));
        AssertEx.Equal(string.Empty, residue.Flush());
    }
}
