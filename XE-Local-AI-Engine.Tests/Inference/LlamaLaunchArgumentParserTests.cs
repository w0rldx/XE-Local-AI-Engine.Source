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
    // Reachability family.
    [Arguments("-m /etc/passwd", "-m")]
    [Arguments("--model foo.gguf", "--model")]
    [Arguments("--host 0.0.0.0", "--host")]
    [Arguments("--port 9999", "--port")]
    [Arguments("--top-k 40 --host=0.0.0.0", "--host")]
    // Memory-fit placement family — owned by the allocator/policy, so also rejected.
    [Arguments("-c 8192", "-c")]
    [Arguments("--ctx-size 8192", "--ctx-size")]
    [Arguments("-ngl 20", "-ngl")]
    [Arguments("--n-gpu-layers 20", "--n-gpu-layers")]
    [Arguments("--flash-attn on", "--flash-attn")]
    [Arguments("--parallel 4", "--parallel")]
    [Arguments("-ctk q8_0", "-ctk")]
    [Arguments("--top-k 40 -ub 2048", "-ub")]
    // Expert placement — --cpu-moe/--n-cpu-moe write the SAME tensor-override list -ot does (llama.cpp
    // common/arg.cpp), so an override could re-place every expert after the placement verdict was admitted.
    [Arguments("--cpu-moe", "--cpu-moe")]
    [Arguments("-cmoe", "-cmoe")]
    [Arguments("--top-k 40 --cpu-moe", "--cpu-moe")]
    [Arguments("--n-cpu-moe 12", "--n-cpu-moe")]
    [Arguments("-ncmoe 12", "-ncmoe")]
    // Adapter family — newly reserved. The registry decides which adapter (if any) a model launches with, and the
    // launch-policy fingerprint commits to that choice, so an operator override here is now rejected.
    [Arguments("--lora /tmp/tuned.gguf", "--lora")]
    [Arguments("--lora-scaled /tmp/tuned.gguf", "--lora-scaled")]
    [Arguments("--top-k 40 --lora=/tmp/tuned.gguf", "--lora")]
    public void FindReservedFlag_DetectsManagedFlags(string raw, string expected)
    {
        AssertEx.Equal(expected, LlamaLaunchArgumentParser.FindReservedFlag(raw));
    }

    [Test]
    public void ParseSanitized_StripsExpertPlacementFlags()
    {
        var valueless = LlamaLaunchArgumentParser.ParseSanitized("--temp 0.7 --cpu-moe --top-k 40");

        AssertEx.Equal(expected: 4, valueless.Count);
        AssertEx.False(valueless.Contains("--cpu-moe"), "The expert-placement flag must be stripped on the read path.");

        var counted = LlamaLaunchArgumentParser.ParseSanitized("--temp 0.7 -ncmoe 12 --top-k 40");

        AssertEx.Equal(expected: 4, counted.Count);
        AssertEx.False(counted.Contains("-ncmoe"), "The counted form must be stripped too.");
        AssertEx.False(counted.Contains("12"), "Its value must be stripped with it.");
    }

    [Test]
    public void ParseSanitized_StripsLora_WithItsValue()
    {
        var result = LlamaLaunchArgumentParser.ParseSanitized("--temp 0.7 --lora /tmp/tuned.gguf --top-k 40");

        AssertEx.Equal(expected: 4, result.Count);
        AssertEx.False(result.Contains("--lora"), "The adapter flag must be stripped on the read path.");
        AssertEx.False(result.Contains("/tmp/tuned.gguf"), "Its value must be stripped with it.");
    }

    [Test]
    // Sampling / decoding / RoPE tuning stays available — that is the experiment.
    [Arguments("--top-k 40 --repeat-penalty 1.1")]
    [Arguments("--rope-freq-base 10000")]
    [Arguments("--temp 0.7 --min-p 0.05")]
    [Arguments("--samplers top_k")]
    public void FindReservedFlag_WhenNoneManaged_ReturnsNull(string raw)
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
    public void ParseSanitized_StripsMemoryFitPlacementFlag_KeepingSamplingFlags()
    {
        // A placement flag (owned by the allocator) is stripped with its value even mid-string; sampling flags survive.
        var result = LlamaLaunchArgumentParser.ParseSanitized("--top-k 40 -ngl 999 --repeat-penalty 1.1");

        AssertEx.False(result.Contains("-ngl"), "The memory-fit -ngl flag must be stripped.");
        AssertEx.False(result.Contains("999"), "The stripped flag's value goes with it.");
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
