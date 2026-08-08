namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     The wire surface an Azure Foundry / Azure OpenAI connection targets.
/// </summary>
public enum AzureFoundryApiSurface
{
    /// <summary>
    ///     The classic Azure OpenAI deployments surface (<c>{endpoint}/openai/deployments/{deployment}/...</c>),
    ///     built via <c>Azure.AI.OpenAI.AzureOpenAIClient</c>. The default, so a legacy stored connection with no
    ///     <c>ApiSurface</c> field deserializes to this value unchanged.
    /// </summary>
    AzureDeployments = 0,

    /// <summary>
    ///     The OpenAI-compatible v1 surface (<c>{endpoint}/openai/v1/chat/completions</c>, deployment name carried
    ///     in the request body's <c>model</c> field instead of the URL path), built via the plain OpenAI .NET SDK
    ///     client. Intended for a gateway (e.g. an Azure APIM AI gateway) that only routes <c>/openai/v1/*</c>.
    /// </summary>
    OpenAiV1 = 1,
}
