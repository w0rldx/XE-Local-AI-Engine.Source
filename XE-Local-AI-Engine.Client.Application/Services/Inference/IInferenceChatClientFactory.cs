namespace XE_Local_AI_Engine.Client.Services.Inference;

using Microsoft.Extensions.AI;

/// <summary>
///     Builds an <see cref="IChatClient" /> over a transient profiling llama-server endpoint's OpenAI-compatible base
///     URL.
/// </summary>
/// <remarks>
///     A seam rather than a direct dependency on the provider's INTERNAL adapter factory, so the benchmark harness
///     can be unit-tested with a fake chat client while production wires the real Microsoft.Extensions.AI OpenAI
///     adapter.
/// </remarks>
public interface IInferenceChatClientFactory
{
    /// <summary>Builds an OpenAI-chat <see cref="IChatClient" /> pointed at <paramref name="baseAddress" /> (the <c>…/v1</c> URL).</summary>
    IChatClient CreateChatClient(Uri baseAddress, string modelId);
}
