namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using XE_Local_AI_Engine.Providers.CodexOAuth;

/// <summary>
///     Declared capability matrix for the Azure Foundry / Azure OpenAI provider. Mirrors
///     <see cref="CodexProviderCapabilities" />: the values live on the provider rather than the shared
///     <c>LocalModelDescriptor</c> contract because Azure is a cloud provider the local runtime never classifies.
/// </summary>
/// <remarks>
///     Both surfaces that must agree on Azure capabilities read <see cref="V0" /> directly: the chat capability gate
///     (<c>ModelCapabilityResolver.ResolveAsync</c>) and the <c>/models</c> <c>IsToolCapable</c> tag in
///     <c>LocalModelsMapper</c>. Tool calling is enabled for all Azure
///     OpenAI chat deployments (the MEAI Azure OpenAI pipeline supports function invocation). Reasoning / extended
///     thinking is NOT advertised here: a generic Azure OpenAI chat deployment (GPT-4 class) is not a reasoning model,
///     and the Ollama-shaped <c>think</c> property is meaningless on the Azure wire, so the gate keeps thinking off.
/// </remarks>
public static class AzureFoundryProviderCapabilities
{
    public static AgentModelCapabilities V0 { get; } = new()
    {
        SupportsStreaming = true,
        SupportsToolCalling = true,
        SupportsParallelToolCalls = false,
        SupportsStructuredOutput = false,
        SupportsVision = false,
        SupportsUsage = false,
        SupportsServiceSideThreads = false
    };
}
