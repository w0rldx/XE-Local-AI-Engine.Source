namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Text;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The context-size rejection bodies the provider boundary recovers from, and the ones it must leave alone.</summary>
[Category(TestCategories.Unit)]
public sealed class ProviderContextOverflowTests
{
    [Test]
    [Arguments("""{"error":{"code":400,"message":"request (9000 tokens) exceeds the available context size (8192 tokens)","type":"exceed_context_size_error","n_prompt_tokens":9000,"n_ctx":8192}}""")]
    [Arguments("""{"error":{"code":400,"type":"exceed_context_size_error"},"n_prompt_tokens":9000,"n_ctx":8192}""")]
    public void Parse_LlamaServerBody_ReadsTheCountsBesideTheTypeOrAtTheTopLevel(string body)
    {
        var overflow = AssertEx.NotNull(ProviderContextOverflow.From(Error(400, body)));

        AssertEx.Equal(9_000, overflow.PromptTokens);
        AssertEx.Equal(8_192, overflow.ContextTokens);
    }

    [Test]
    [Arguments("""{"error":{"message":"too long","type":"invalid_request_error","code":"context_length_exceeded"}}""")]
    [Arguments("""{"error":{"type":"exceed_context_size_error","n_prompt_tokens":0,"n_ctx":-1}}""")]
    public void Parse_WithoutPositiveCounts_MatchesWithNoNumbers(string body)
    {
        var overflow = AssertEx.NotNull(ProviderContextOverflow.From(Error(400, body)));

        AssertEx.Null(overflow.PromptTokens);
        AssertEx.Null(overflow.ContextTokens);
    }

    [Test]
    [Arguments(400, """{"error":{"type":"invalid_request_error","message":"'messages' must not be empty"}}""")]
    [Arguments(400, "not json")]
    [Arguments(400, "")]
    [Arguments(500, """{"error":{"type":"exceed_context_size_error"}}""")]
    public void From_AnyOtherStatusOrBody_IsNotAnOverflow(int status, string body)
    {
        AssertEx.Null(ProviderContextOverflow.From(Error(status, body)));
    }

    private static ProviderErrorBody Error(int status, string body) =>
        new()
        {
            Status = status,
            Body = Encoding.UTF8.GetBytes(body)
        };
}
