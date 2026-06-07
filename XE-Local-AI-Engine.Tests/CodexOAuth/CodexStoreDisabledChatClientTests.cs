namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using Microsoft.Extensions.AI;
using OpenAI.Responses;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Tests.CloudProviders;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
/// Proves <c>store=false</c> reaches the request options on every Codex call (plan §10/§12/R6) — asserting the
/// flag on the options the OpenAI Responses mapper consumes, not trusting the wrapper's name. The store-disabling
/// wrapper sets <see cref="ChatOptions.RawRepresentationFactory"/> to produce a
/// <see cref="CreateResponseOptions"/> with <see cref="CreateResponseOptions.StoredOutputEnabled"/> false.
/// </summary>
public sealed class CodexStoreDisabledChatClientTests
{
    [Test]
    public async Task GetResponse_AppliesStoredOutputDisabled_ToTheRequestOptions()
    {
        using var inner = new StubChatClient();
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        AssertStoreDisabled(inner.LastOptions);
    }

    [Test]
    public async Task GetStreamingResponse_AppliesStoredOutputDisabled_ToTheRequestOptions()
    {
        using var inner = new StubChatClient();
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            // Drain the stream so the wrapper forwards the options.
        }

        AssertStoreDisabled(inner.LastOptions);
    }

    [Test]
    public async Task GetResponse_WhenCallerSuppliesOptions_StillForcesStoreDisabled()
    {
        using var inner = new StubChatClient();
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");
        var callerOptions = new ChatOptions { Temperature = 0.5f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], callerOptions);

        AssertStoreDisabled(inner.LastOptions);
    }

    [Test]
    public async Task GetResponse_WhenCallerSuppliesLocalModelId_OverwritesWithPinnedCodexModel()
    {
        using var inner = new StubChatClient();
        using var client = new CodexStoreDisabledChatClient(inner, "gpt-5.4");

        // The agent send path leaks a LOCAL Ollama model name via ChatOptions.ModelId; the wrapper must pin Codex.
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            new ChatOptions { ModelId = "qwen3:8b" });

        var forwarded = AssertEx.NotNull(inner.LastOptions);
        AssertEx.Equal("gpt-5.4", forwarded.ModelId);
    }

    private static void AssertStoreDisabled(ChatOptions? options)
    {
        var forwarded = AssertEx.NotNull(options);
        var factory = AssertEx.NotNull(forwarded.RawRepresentationFactory, "store-disabling RawRepresentationFactory must be set");

        // RawRepresentationFactory takes the target IChatClient; the wrapper ignores it and returns the base options.
        using var probe = new StubChatClient();
        var raw = factory(probe);

#pragma warning disable OPENAI001 // CreateResponseOptions is the (experimental) options object the Responses mapper consumes.
        var responseOptions = raw as CreateResponseOptions
            ?? throw new AssertionException($"Expected CreateResponseOptions, got {raw?.GetType().Name ?? "<null>"}.");

        AssertEx.Equal<bool?>(false, responseOptions.StoredOutputEnabled);
#pragma warning restore OPENAI001
    }
}
