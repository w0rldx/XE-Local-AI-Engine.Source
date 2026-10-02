namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The ONE node-locality predicate for the background model steps (playbook analysis, playbook eval, memory
///     extraction): their input is conversation content, so it must never reach a model that leaves the node.
/// </summary>
/// <remarks>
///     A model passes only when it carries no <c>ext:</c> scheme AND <see cref="IModelTrustResolver" /> resolves it to
///     <see cref="ModelTrustLocality.Local" />. The <c>ext:</c> refusal is deliberate even for an external connection
///     declared Local: <c>LocalModelProviderResolver</c> routes every <c>ext:</c> id to the external OpenAI-compatible
///     provider, which is not the node-local provider path these steps promise. The node-settings accessor uses the same
///     predicate to decide whether a background picker may inherit the default chat model.
/// </remarks>
internal static class BackgroundModelLocalityGuard
{
    /// <summary>Whether <paramref name="modelName" /> stays on the node-local provider path.</summary>
    public static async Task<bool> IsNodeLocalAsync(string? modelName, IModelTrustResolver modelTrustResolver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(modelTrustResolver);

        return !ExternalModelId.HasExternalScheme(modelName)
               && await modelTrustResolver.ResolveAsync(modelName, cancellationToken) == ModelTrustLocality.Local;
    }

    /// <summary>
    ///     Whether the background step may run on <paramref name="modelName" />; when it may not, logs one warning naming
    ///     the model and the node setting that selected it, and the caller returns its "nothing produced" result.
    /// </summary>
    public static async Task<bool> AllowAsync(string modelName,
        string settingName,
        IModelTrustResolver modelTrustResolver,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (await IsNodeLocalAsync(modelName, modelTrustResolver, cancellationToken))
        {
            return true;
        }

        logger.LogWarning("Skipped a background step: model {ModelName} selected by node setting {SettingName} is not node-local, and background analysis, eval and memory extraction never leave the node.",
            modelName, settingName);
        return false;
    }
}
