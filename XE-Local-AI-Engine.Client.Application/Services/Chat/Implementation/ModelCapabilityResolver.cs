namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

/// <summary>
///     Default <see cref="IModelCapabilityResolver" />, routing the capability lookup by the model's provider so an
///     id is never classified against a runtime that has never seen it.
/// </summary>
/// <remarks>
///     Codex and Azure Foundry ids use their declared matrices, an external OpenAI-compatible model uses the
///     operator's own declarations, and a llama.cpp GGUF reads its offline chat-template capabilities with no
///     network. This is the one provider-routing decision in the codebase: both the orchestration resolver, per participant, and
///     <see cref="ChatTurnResolver" />, per turn, resolve through it.
/// </remarks>
public sealed class ModelCapabilityResolver : IModelCapabilityResolver
{
    // What an Unresolved id gets: not capable, and cloud, so the private-data gates withhold. Reached for a deleted
    // connection, a corrupt store, a hand-edited id in a saved agent, or a cloud routing read that threw.
    private static readonly ModelCapabilitySnapshot Unresolved = new(SupportsThinking: false, SupportsTools: false, IsCloud: true)
    {
        ReasoningBudgetEnforceable = true
    };

    // The safe default: not thinking-capable, not tool-capable, and node-local.
    private static readonly ModelCapabilitySnapshot NotCapableLocal = new(SupportsThinking: false, SupportsTools: false, IsCloud: false);
    private readonly IGgufModelCapabilityResolver _ggufModelCapabilityResolver;
    private readonly IModelTrustResolver _modelTrustResolver;

    public ModelCapabilityResolver(IGgufModelCapabilityResolver ggufModelCapabilityResolver,
        IModelTrustResolver modelTrustResolver)
    {
        _ggufModelCapabilityResolver = ggufModelCapabilityResolver;
        _modelTrustResolver = modelTrustResolver;
    }

    public async Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return NotCapableLocal;
        }

        // A Codex cloud model uses its declared matrix. Thinking is on and tools track the V0 matrix; a cloud provider ignores `think`.
        if (CodexModelCatalog.IsCodexModel(model))
        {
            return new ModelCapabilitySnapshot(SupportsThinking: true, CodexProviderCapabilities.V0.SupportsToolCalling, IsCloud: true)
            {
                // The reasoning-budget marker is consumed only by the llama.cpp chat client, so it never reaches the
                // Codex wire; report the budget enforceable so the cloud path stays byte-identical.
                ReasoningBudgetEnforceable = true
            };
        }

        // An external OpenAI-compatible model is classified from the OPERATOR'S declarations, never a probe. It is
        // checked before cloud routing, which an ext: id falls through and would report a hosted endpoint as local.
        if (ExternalModelId.HasExternalScheme(model))
        {
            if (await _modelTrustResolver.FindExternalAsync(model, cancellationToken) is not { } registration)
            {
                return Unresolved;
            }

            return new ModelCapabilitySnapshot(registration.Model.SupportsReasoning,
                registration.Model.SupportsTools,
                registration.Connection.Locality == ExternalProviderLocality.Cloud)
            {
                SupportsVision = registration.Model.SupportsVision,
                CloudGrants = registration.Connection.CloudGrants,
                // Vacuously enforceable: this provider emits no llama-server reasoning-budget field, so no cap can be
                // silently accepted and ignored, and reporting false would suppress a marker nothing here reads.
                ReasoningBudgetEnforceable = true
            };
        }

        // Cloud LOCALITY comes from the trust resolver (the factory's own routing snapshot past the Codex branch above, so
        // Cloud here is an Azure deployment). A routing read failure is Unresolved and FAILS CLOSED: cloud, no tools.
        var locality = await _modelTrustResolver.ResolveAsync(model, cancellationToken);
        if (locality == ModelTrustLocality.Unresolved)
        {
            return Unresolved;
        }

        if (locality == ModelTrustLocality.Cloud)
        {
            return new ModelCapabilitySnapshot(SupportsThinking: false, AzureFoundryProviderCapabilities.V0.SupportsToolCalling, IsCloud: true);
        }

        // A node-local model reads its chat template offline. Vision rides the GGUF descriptor's projector-gated flag — the only
        // path that can advertise it — and an id with no installed GGUF takes the safe default.
        var ggufCapabilities = await _ggufModelCapabilityResolver
            .ResolveOrNullAsync(model, cancellationToken);
        return ggufCapabilities is { } caps
            ? new ModelCapabilitySnapshot(caps.SupportsThinking, caps.SupportsTools, IsCloud: false)
            {
                SupportsVision = caps.SupportsVision,
                // The ONE path that can report a budget as unenforceable: llama.cpp is the only runtime that reads
                // the budget, and the GGUF descriptor is the only place its chat template was classified.
                ReasoningBudgetEnforceable = caps.ReasoningBudgetEnforceable
            }
            : NotCapableLocal;
    }
}
