namespace XE_Local_AI_Engine.Tests.Inference;

using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the per-model extra-launch-arg parser: quote-aware tokenizing, detecting the reserved process-contract
///     flags on the write path, and defensively stripping them (with their space-separated value) on the read path.
/// </summary>
public sealed class LlamaLaunchArgumentParserTests
{
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public void Tokenize_WhenBlank_ReturnsEmpty(string? raw)
    {
        AssertEx.Equal(expected: 0, LlamaLaunchArgumentParser.Tokenize(raw).Count);
    }

    [Test]
    public void Tokenize_SplitsOnWhitespace_AndHonorsQuotes()
    {
        var tokens = LlamaLaunchArgumentParser.Tokenize("--top-k 40   -ot \"\\.ffn.*=CPU\" --repeat-penalty 1.1");

        AssertEx.Equal(expected: 6, tokens.Count);
        AssertEx.Equal("--top-k", tokens[0]);
        AssertEx.Equal("40", tokens[1]);
        AssertEx.Equal("-ot", tokens[2]);
        // The quoted value is one token with the quotes stripped, so an override tensor regex with spaces survives.
        AssertEx.Equal("\\.ffn.*=CPU", tokens[3]);
        AssertEx.Equal("--repeat-penalty", tokens[4]);
        AssertEx.Equal("1.1", tokens[5]);
    }

    [Test]
    [Arguments("-m /etc/passwd", "-m")]
    [Arguments("--model foo.gguf", "--model")]
    [Arguments("--host 0.0.0.0", "--host")]
    [Arguments("--port 9999", "--port")]
    [Arguments("--top-k 40 --host=0.0.0.0", "--host")]
    public void FindReservedFlag_DetectsReservedFlags(string raw, string expected)
    {
        AssertEx.Equal(expected, LlamaLaunchArgumentParser.FindReservedFlag(raw));
    }

    [Test]
    [Arguments("--top-k 40 --repeat-penalty 1.1")]
    [Arguments("--rope-freq-base 10000")]
    [Arguments("--flash-attn on")]
    public void FindReservedFlag_WhenNoneReserved_ReturnsNull(string raw)
    {
        AssertEx.Null(LlamaLaunchArgumentParser.FindReservedFlag(raw));
    }

    [Test]
    public void ParseSanitized_KeepsNonReservedFlags_Verbatim()
    {
        var result = LlamaLaunchArgumentParser.ParseSanitized("--top-k 40 --repeat-penalty 1.1");

        AssertEx.Equal(expected: 4, result.Count);
        AssertEx.Equal("--top-k", result[0]);
        AssertEx.Equal("40", result[1]);
        AssertEx.Equal("--repeat-penalty", result[2]);
        AssertEx.Equal("1.1", result[3]);
    }

    [Test]
    public void ParseSanitized_DropsReservedFlagAndItsSpaceSeparatedValue()
    {
        // Defense-in-depth: even if a reserved flag reached the store, it never reaches the process. The value token
        // following a bare reserved flag is dropped with it; the surrounding non-reserved flags are untouched.
        var result = LlamaLaunchArgumentParser.ParseSanitized("--top-k 40 --host 0.0.0.0 --repeat-penalty 1.1");

        AssertEx.False(result.Contains("--host"), "The reserved --host flag must be stripped.");
        AssertEx.False(result.Contains("0.0.0.0"), "The reserved flag's value must be stripped with it.");
        AssertEx.Equal(expected: 4, result.Count);
        AssertEx.Equal("--top-k", result[0]);
        AssertEx.Equal("--repeat-penalty", result[2]);
    }

    [Test]
    public void ParseSanitized_DropsEqualsJoinedReservedFlag_WithoutConsumingNextToken()
    {
        // A `--port=9999` token carries its own value, so nothing extra is consumed — the following flag survives.
        var result = LlamaLaunchArgumentParser.ParseSanitized("--port=9999 --top-k 40");

        AssertEx.False(result.Contains("--port=9999"), "The =-joined reserved flag must be stripped.");
        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.Equal("--top-k", result[0]);
        AssertEx.Equal("40", result[1]);
    }
}
