namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

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

        var openAiClient = new OpenAIClient(new ApiKeyCredential(IgnoredApiKey), new OpenAIClientOptions
        {
            Endpoint = baseAddress
        });
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }
}
