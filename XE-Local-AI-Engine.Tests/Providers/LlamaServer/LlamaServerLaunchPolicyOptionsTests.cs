namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Validation + role-mapping coverage for <see cref="LlamaServerLaunchPolicyOptions" /> (AUD4-02/05/17).</summary>
public sealed class LlamaServerLaunchPolicyOptionsTests
{
    [Test]
    public void Validate_DefaultOptions_Passes()
    {
        new LlamaServerLaunchPolicyOptions().Validate();
    }

    [Test]
    public async Task Validate_NonPositiveChatContext_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions { ChatContextTokens = 0 });
    }

    [Test]
    public async Task Validate_NegativeSafetyMargin_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions { ContextSafetyMarginTokens = -1 });
    }

    [Test]
    public async Task Validate_EmptyKvCacheType_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions { KvCacheType = "  " });
    }

    [Test]
    public async Task Validate_NegativeThreadReserve_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions { CpuThreadReserve = -1 });
    }

    [Test]
    public async Task Validate_NonPositiveExplicitThreadCount_Throws()
    {
        await AssertValidationThrowsAsync(new LlamaServerLaunchPolicyOptions { CpuThreadCount = 0 });
    }

    [Test]
    public void ContextTokensForRole_MapsEachRoleToItsDefault()
    {
        var options = new LlamaServerLaunchPolicyOptions();

        AssertEx.Equal(options.ChatContextTokens, options.ContextTokensForRole(ModelRole.Chat));
        AssertEx.Equal(options.EmbeddingContextTokens, options.ContextTokensForRole(ModelRole.Embedding));
        AssertEx.Equal(options.RerankerContextTokens, options.ContextTokensForRole(ModelRole.Reranker));
    }

    private static Task AssertValidationThrowsAsync(LlamaServerLaunchPolicyOptions options)
    {
        // Validate() throws synchronously; ThrowsAsync catches a synchronous throw from the delegate too.
        return AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
    }
}
