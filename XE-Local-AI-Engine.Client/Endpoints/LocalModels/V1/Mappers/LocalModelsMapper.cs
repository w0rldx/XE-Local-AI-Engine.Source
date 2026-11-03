namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

internal static class LocalModelsMapper
{
    /// <summary>
    ///     Builds the list. Order: local GGUF (llamacpp), cloud (Codex keeps its catalog's strongest-first order), external,
    ///     which the client sections per connection and badges by its DECLARED locality.
    /// </summary>
    public static ListLocalModelsResponse ToListResponse(string? selectedModelName,
        string? configuredDefaultModelName,
        IReadOnlyDictionary<string, ModelClassificationRecord> classifications,
        IReadOnlyList<LocalModelResponse>? cloudModels = null,
        IReadOnlyList<LocalModelDescriptor>? ggufModels = null,
        IReadOnlyList<LocalModelResponse>? externalModels = null)
    {
        ArgumentNullException.ThrowIfNull(classifications);

        var ggufItems = ggufModels is { Count: > 0 }
            ? ToLlamaCppModelResponses(ggufModels, selectedModelName, classifications)
            : [];

        return new ListLocalModelsResponse
        {
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Items = ConcatRemoteModels(ggufItems, cloudModels, externalModels)
        };
    }

    /// <summary>
    ///     Maps installed GGUF models (served by the bundled llama.cpp runtime) to model-list entries tagged
    ///     <see cref="LocalModelProviders.LlamaCpp" />.
    /// </summary>
    /// <remarks>
    ///     A GGUF's kind comes from the shared offline rule: operator override, then the name rules; the picker shows
    ///     only Chat. Reasoning, tool support and the capability tokens are detected offline from the descriptor's chat
    ///     template; an unreadable template defaults to the safe no-tools/no-reasoning classification. Reasoning surfaces as two mutually
    ///     exclusive flags: graded <see cref="LocalModelResponse.IsReasoningCapable" />, template-baked <see cref="LocalModelResponse.IsNativeReasoningCapable" />.
    /// </remarks>
    public static IReadOnlyList<LocalModelResponse> ToLlamaCppModelResponses(IReadOnlyList<LocalModelDescriptor> ggufModels,
        string? selectedModelName,
        IReadOnlyDictionary<string, ModelClassificationRecord> classifications)
    {
        ArgumentNullException.ThrowIfNull(ggufModels);
        ArgumentNullException.ThrowIfNull(classifications);

        return ggufModels
               .Where(static descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName))
               .Select(descriptor =>
               {
                   var classification = classifications.GetValueOrDefault(descriptor.ModelName);
                   var resolved = LocalGgufModelKindClassifier.ResolveKind(descriptor.ModelName, classification, descriptor.DecisionType);
                   var kind = resolved.Kind;
                   return new LocalModelResponse
                   {
                       ModelName = descriptor.ModelName,
                       Provider = LocalModelProviders.LlamaCpp,
                       SizeBytes = descriptor.SizeBytes,
                       ModifiedAtUtc = descriptor.ModifiedAt?.ToUnixTimeMilliseconds(),
                       Origin = descriptor.Origin,
                       ModelContentFingerprint = descriptor.ModelContentFingerprint,
                       IsSelected = string.Equals(descriptor.ModelName, selectedModelName, StringComparison.OrdinalIgnoreCase),
                       Kind = kind.ToString(),
                       DetectedKind = resolved.DetectedKind.ToString(),
                       // The template detector adds "completion" to every GGUF; an embedding, reranker or decision head generates no text. A decision model
                       // (Kev-4B) can still carry its base model's chat template, so it reports none of the template-derived chat capabilities either.
                       Capabilities = GgufCapabilitiesFor(kind, descriptor.Capabilities),
                       IsReasoningCapable = kind != ModelKind.Decision && descriptor.IsReasoningCapable,
                       IsNativeReasoningCapable = kind != ModelKind.Decision && descriptor.IsNativeReasoningCapable,
                       // Detected from the SAME chat template as the reasoning flags: a graded model whose template renders no reasoning end marker keeps its effort but
                       // loses its token cap, and the node says so rather than letting the UI imply a budget that llama.cpp silently ignores.
                       ReasoningBudgetEnforceable = descriptor.ReasoningBudgetEnforceable,
                       IsToolCapable = kind != ModelKind.Decision && descriptor.IsToolCapable,
                       IsMultimodalCapable = descriptor.IsMultimodalCapable,
                       Family = descriptor.Family,
                       ParameterSize = descriptor.ParameterSize,
                       QuantizationLevel = descriptor.QuantizationLevel,
                       IsOverridden = resolved.IsOverridden
                   };
               })
               .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
               .ToArray();
    }

    private static IReadOnlyList<string> GgufCapabilitiesFor(ModelKind kind, IReadOnlyList<string> detected) =>
        kind switch
        {
            ModelKind.Decision => [],
            ModelKind.Embedding or ModelKind.Reranker => [.. detected.Where(static capability => !string.Equals(capability, "completion", StringComparison.Ordinal))],
            _ => detected
        };

    // Appends the two non-node-local families after the node-local ones: cloud (Codex, Azure) first, then the operator's external connections.
    private static LocalModelResponse[] ConcatRemoteModels(IReadOnlyList<LocalModelResponse> localItems,
        IReadOnlyList<LocalModelResponse>? cloudModels,
        IReadOnlyList<LocalModelResponse>? externalModels)
    {
        IEnumerable<LocalModelResponse> items = localItems;
        if (cloudModels is { Count: > 0 })
        {
            items = items.Concat(cloudModels);
        }

        if (externalModels is { Count: > 0 })
        {
            items = items.Concat(externalModels);
        }

        return items.ToArray();
    }

    /// <summary>
    ///     Maps the models registered on the operator's external OpenAI-compatible connections to model-list entries
    ///     tagged <see cref="LocalModelProviders.External" />, addressed by their namespaced
    ///     <c>ext:{connectionId}/{wireId}</c> id.
    /// </summary>
    /// <remarks>
    ///     Every capability here is DECLARED by the operator, never probed: only <c>POST /v1/chat/completions</c> is
    ///     universal across OpenAI-compatible servers, and none advertises tool, vision or reasoning support in a shape
    ///     trustworthy across llama.cpp, vLLM, LM Studio and hosted APIs alike. Reasoning maps onto the GRADED flag and
    ///     never the native one, a llama.cpp chat-template concept, and <c>ReasoningBudgetEnforceable</c> is vacuously
    ///     true: no budget marker means no cap to silently ignore. Size, quantization and modified-at stay null.
    /// </remarks>
    public static IReadOnlyList<LocalModelResponse> ToExternalProviderModelResponses(IReadOnlyList<ExternalProviderModelRegistration> registrations,
        string? selectedModelName)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        return
        [
            .. registrations.Select(registration => new LocalModelResponse
            {
                ModelName = registration.ModelId,
                Provider = LocalModelProviders.External,
                DisplayLabel = registration.Model.DisplayName,
                ExternalConnectionId = registration.Connection.Id,
                ExternalConnectionName = registration.Connection.DisplayName,
                DeclaredLocality = ToDeclaredLocality(registration.Connection.Locality),
                IsSelected = string.Equals(registration.ModelId, selectedModelName, StringComparison.OrdinalIgnoreCase),
                Kind = ModelKind.Chat.ToString(),
                DetectedKind = ModelKind.Chat.ToString(),
                Capabilities = [],
                IsReasoningCapable = registration.Model.SupportsReasoning,

                // The one capability pair only an external declaration can express: an endpoint that reasons but
                // ignores `reasoning_effort` must get the binary control, not a graded menu of inert levels.
                IsReasoningEffortCapable = registration.Model.SupportsReasoningEffort,
                IsNativeReasoningCapable = false,
                ReasoningBudgetEnforceable = true,
                IsToolCapable = registration.Model.SupportsTools,
                IsMultimodalCapable = registration.Model.SupportsVision,
                IsOverridden = false
            })
        ];
    }

    /// <summary>Maps the declared locality onto its lowercase wire value.</summary>
    private static string ToDeclaredLocality(ExternalProviderLocality locality)
    {
        return locality == ExternalProviderLocality.Local
            ? LocalModelDeclaredLocalities.Local

            // Anything that is not a positive Local declaration is treated as cloud — the fail-closed direction the trust resolver takes for an unresolvable id, kept identical
            // here so the badge can never say "local" about something the gates treat as leaving the node.
            : LocalModelDeclaredLocalities.Cloud;
    }

    /// <summary>
    ///     Maps the offered Codex cloud models (<see cref="CodexModelCatalog.ModelIds" />) to model-list entries tagged
    ///     <see cref="LocalModelProviders.CodexOAuth" />.
    /// </summary>
    /// <remarks>
    ///     The endpoint passes these only when a Codex session is present. Each entry advertises the Codex provider's
    ///     declared capability matrix (<see cref="CodexProviderCapabilities.V0" />), because the local runtime has
    ///     never seen these ids. Size and quantization stay null: they
    ///     are local-runtime concepts.
    /// </remarks>
    public static IReadOnlyList<LocalModelResponse> ToCodexCloudModelResponses(string? selectedModelName)
    {
        return CodexModelCatalog.ModelIds
                                .Select(modelId => new LocalModelResponse
                                {
                                    ModelName = modelId,
                                    Provider = LocalModelProviders.CodexOAuth,
                                    IsSelected = string.Equals(modelId, selectedModelName, StringComparison.OrdinalIgnoreCase),
                                    Kind = ModelKind.Chat.ToString(),
                                    DetectedKind = ModelKind.Chat.ToString(),
                                    Capabilities = [],
                                    IsReasoningCapable = true,
                                    IsToolCapable = CodexProviderCapabilities.V0.SupportsToolCalling,
                                    IsOverridden = false
                                })
                                .ToArray();
    }

    /// <summary>
    ///     Maps a stored Azure Foundry connection's manually-added deployments to model-list entries tagged
    ///     <see cref="LocalModelProviders.AzureFoundry" />.
    /// </summary>
    /// <remarks>
    ///     The endpoint passes these only when an Azure connection is stored. Each entry advertises the Azure
    ///     provider's declared capability matrix (<see cref="AzureFoundryProviderCapabilities.V0" />), because the
    ///     local runtime has never seen these deployment ids. The deployment name is
    ///     the model id and an optional display label rides along; size and quantization stay null, being
    ///     local-runtime concepts.
    /// </remarks>
    public static IReadOnlyList<LocalModelResponse> ToAzureFoundryCloudModelResponses(StoredAzureFoundryConnection connection,
        string? selectedModelName)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.Models
                         .Where(static model => !string.IsNullOrWhiteSpace(model.DeploymentName))
                         .Select(model => new LocalModelResponse
                         {
                             ModelName = model.DeploymentName,
                             Provider = LocalModelProviders.AzureFoundry,

                             // The operator sets this label in the Azure settings editor; it is carried here so the picker can render it, rather than being stored,
                             // round-tripped through settings and then dropped.
                             DisplayLabel = model.DisplayLabel,
                             IsSelected = string.Equals(model.DeploymentName, selectedModelName, StringComparison.OrdinalIgnoreCase),
                             Kind = ModelKind.Chat.ToString(),
                             DetectedKind = ModelKind.Chat.ToString(),
                             Capabilities = [],
                             IsReasoningCapable = false,
                             IsToolCapable = AzureFoundryProviderCapabilities.V0.SupportsToolCalling,
                             IsOverridden = false
                         })
                         .ToArray();
    }

    public static ModelKindResponse ToKindResponse(this ModelClassificationResult classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return new ModelKindResponse
        {
            ModelName = classification.ModelName,
            Kind = classification.Kind.ToString(),
            DetectedKind = classification.DetectedKind.ToString(),
            Capabilities = classification.Capabilities,
            IsOverridden = classification.IsOverridden
        };
    }

    /// <summary>
    ///     Parses the wire kind of a classification override, refusing anything that is not a defined name.
    /// </summary>
    /// <remarks>
    ///     <c>Enum.TryParse</c> accepts numeric strings for undefined values (for example "99"), so
    ///     <c>Enum.IsDefined</c> guards it. <c>Unknown</c> is allowed: it is the operator's explicit "I don't know".
    ///     The request validator and the handler read the same parse, so the value that was accepted is the value that
    ///     is stored.
    /// </remarks>
    public static bool TryParseKind(string? value, out ModelKind kind)
    {
        kind = ModelKind.Unknown;

        return !string.IsNullOrWhiteSpace(value)
               && Enum.TryParse(value, ignoreCase: true, out kind)
               && Enum.IsDefined(kind);
    }

    /// <summary>Maps an installed GGUF descriptor (served by llama.cpp) to the shared model-details response.</summary>
    /// <remarks>
    ///     <see cref="LocalModelDetailsResponse.MaxContextTokens" /> is the descriptor's advertised train ceiling and
    ///     <paramref name="effectiveContextTokens" /> the RUNNING process's launched context window, when a chat
    ///     process is warm. <c>Template</c>/<c>System</c>/<c>License</c> have no equivalent on a raw GGUF, so they stay null.
    /// </remarks>
    public static LocalModelDetailsResponse ToDetailsResponse(this LocalModelDescriptor descriptor,
        string modelName,
        int? effectiveContextTokens = null,
        bool? expertsOffloaded = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = descriptor.MaxContextTokens,
            EffectiveContextTokens = effectiveContextTokens,
            ExpertsOffloaded = expertsOffloaded,
            Origin = descriptor.Origin,
            ModelContentFingerprint = descriptor.ModelContentFingerprint,
            Template = null,
            System = null,
            License = null
        };
    }

    /// <summary>
    ///     Maps a built-in cloud model (Codex, Azure Foundry) to the shared model-details response.
    /// </summary>
    /// <remarks>
    ///     The declared window is the only detail, reported as both ceiling and effective window for the same reason as an
    ///     external model's: the node does not launch the process, so the two are one number.
    /// </remarks>
    public static LocalModelDetailsResponse ToCloudDetailsResponse(string modelName, int? contextLength)
    {
        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = contextLength,
            EffectiveContextTokens = contextLength
        };
    }

    /// <summary>
    ///     Maps an external OpenAI-compatible registration to the shared model-details response.
    /// </summary>
    /// <remarks>
    ///     Details for an external model come entirely from the operator's declarations — there is no probe, because
    ///     only POST /v1/chat/completions is universal across OpenAI-compatible servers and none of them reports a
    ///     window in a shape that can be trusted across all of them. The declared window is reported as BOTH the
    ///     advertised ceiling and the effective window: for an endpoint the node does not launch those are the same
    ///     number, and the context meter reads the effective one.
    /// </remarks>
    public static LocalModelDetailsResponse ToDetailsResponse(this ExternalProviderModelRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        return new LocalModelDetailsResponse
        {
            ModelName = registration.ModelId,
            MaxContextTokens = registration.Model.ContextLength,
            EffectiveContextTokens = registration.Model.ContextLength,

            // The same four connection facts the list entry carries: a details view reached directly (a deep link, a reload) has no list entry to read them from, and the
            // egress cue must not depend on which route the client happened to arrive by.
            DisplayLabel = registration.Model.DisplayName,
            ExternalConnectionId = registration.Connection.Id,
            ExternalConnectionName = registration.Connection.DisplayName,
            DeclaredLocality = registration.Connection.Locality == ExternalProviderLocality.Local
                ? LocalModelDeclaredLocalities.Local
                : LocalModelDeclaredLocalities.Cloud,
            IsReasoningEffortCapable = registration.Model.SupportsReasoningEffort
        };
    }
}
