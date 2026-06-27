namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
///     Builds an <see cref="IChatClient" /> over a transient profiling llama-server endpoint's OpenAI-compatible base
///     URL. A seam (rather than a direct dependency on the provider's INTERNAL adapter factory) so the benchmark harness
///     can be unit-tested with a fake chat client while production wires the real Microsoft.Extensions.AI OpenAI adapter.
/// </summary>
public interface IInferenceChatClientFactory
{
    /// <summary>Builds an OpenAI-chat <see cref="IChatClient" /> pointed at <paramref name="baseAddress" /> (the <c>…/v1</c> URL).</summary>
    IChatClient CreateChatClient(Uri baseAddress, string modelId);
}

/// <summary>
///     Default <see cref="IInferenceChatClientFactory" />: mirrors the provider's internal adapter factory by building the
///     Microsoft.Extensions.AI OpenAI chat adapter over the llama-server <c>/v1</c> endpoint. The API key is a sentinel —
///     a localhost llama-server ignores it.
/// </summary>
public sealed class OpenAiInferenceChatClientFactory : IInferenceChatClientFactory
{
    private const string IgnoredApiKey = "ignored";

    /// <inheritdoc />
    public IChatClient CreateChatClient(Uri baseAddress, string modelId)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = new OpenAIClient(new ApiKeyCredential(IgnoredApiKey), new OpenAIClientOptions { Endpoint = baseAddress });
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }
}
