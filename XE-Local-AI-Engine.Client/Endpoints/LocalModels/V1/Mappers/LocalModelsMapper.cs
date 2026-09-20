namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

internal static class LocalModelsMapper
{
    public static ListLocalModelsResponse ToListResponse(IEnumerable<OllamaModelSummary> models,
        string? selectedModelName,
        string? configuredDefaultModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications,
        IReadOnlyList<LocalModelResponse>? cloudModels = null,
        IReadOnlyList<LocalModelDescriptor>? ggufModels = null,
        IReadOnlyList<LocalModelResponse>? externalModels = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(classifications);

        var ollamaItems = models
                          .Where(static model => !string.IsNullOrWhiteSpace(model.Name))
                          .Select(model => model.ToResponse(selectedModelName, classifications))
                          .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                          .ToArray();

        // Order: local Ollama, local GGUF (llamacpp), cloud, external. GGUF names are deduped against the Ollama ones so a name under both runtimes is listed once (Ollama wins),
        // and cloud (Codex) keeps its catalog's strongest-first order. External entries trail everything: the client sections them per connection and badges each by its DECLARED locality.
        var localItems = ConcatGgufModels(ollamaItems, ggufModels, selectedModelName);

        var items = ConcatRemoteModels(localItems, cloudModels, externalModels);

        return new ListLocalModelsResponse
        {
            // A no-Ollama box is still "available" when at least one node-local GGUF is installed — the operator can
            // select and chat over it via llama.cpp without Ollama running.
            IsAvailable = true,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Items = items
        };
    }

    /// <summary>
    ///     Maps installed GGUF models (served by the bundled llama.cpp runtime) to model-list entries tagged
    ///     <see cref="LocalModelProviders.LlamaCpp" />.
    /// </summary>
    /// <remarks>
    ///     GGUF chat models are classified <see cref="ModelKind.Chat" /> WITHOUT an <c>/api/show</c> probe: a
    ///     downloaded GGUF in the chat picker has a completion head by construction. Reasoning, tool support and the
    ///     capability tokens are detected offline from the descriptor's GGUF chat template; an unreadable template
    ///     defaults to the safe no-tools/no-reasoning classification. Reasoning surfaces as two mutually exclusive
    ///     flags: graded <see cref="LocalModelResponse.IsReasoningCapable" />, template-baked <see cref="LocalModelResponse.IsNativeReasoningCapable" />.
    /// </remarks>
    public static IReadOnlyList<LocalModelResponse> ToLlamaCppModelResponses(IReadOnlyList<LocalModelDescriptor> ggufModels,
        string? selectedModelName)
    {
        ArgumentNullException.ThrowIfNull(ggufModels);

        return ggufModels
               .Where(static descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName))
               .Select(descriptor =>
               {
                   // Kind from the name alone (a fresh GGUF carries no probe), per ModelKindDetector.EmbeddingNameFragments and its reranker sibling; the picker shows only Chat. The MTP- draft
                   // quant marker wins, then RERANK before embedding (bge-reranker-… matches both), then a BGE-/BGE: prefix or an EMBED / ALL-MINILM / NOMIC-EMBED / MXBAI-EMBED fragment.
                   var kind = LocalGgufModelKindClassifier.Classify(descriptor.ModelName);
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
                       DetectedKind = kind.ToString(),
                       Capabilities = descriptor.Capabilities,
                       IsReasoningCapable = descriptor.IsReasoningCapable,
                       IsNativeReasoningCapable = descriptor.IsNativeReasoningCapable,
                       // Detected from the SAME chat template as the reasoning flags: a graded model whose template renders no reasoning end marker keeps its effort but
                       // loses its token cap, and the node says so rather than letting the UI imply a budget that llama.cpp silently ignores.
                       ReasoningBudgetEnforceable = descriptor.ReasoningBudgetEnforceable,
                       IsToolCapable = descriptor.IsToolCapable,
                       IsMultimodalCapable = descriptor.IsMultimodalCapable,
                       IsOverridden = false
                   };
               })
               .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
               .ToArray();
    }

    // Appends GGUF entries after the Ollama group, deduping by ModelName (case-insensitive) so a name installed under
    // both runtimes is listed once (the Ollama entry wins). Returns a single ordered array (Ollama first, then GGUF).
    private static LocalModelResponse[] ConcatGgufModels(IReadOnlyList<LocalModelResponse> ollamaItems,
        IReadOnlyList<LocalModelDescriptor>? ggufModels,
        string? selectedModelName)
    {
        if (ggufModels is not { Count: > 0 })
        {
            return ollamaItems.ToArray();
        }

        var ollamaNames = ollamaItems
                          .Select(static item => item.ModelName)
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var ggufItems = ToLlamaCppModelResponses(ggufModels, selectedModelName)
            .Where(item => !ollamaNames.Contains(item.ModelName));

        return ollamaItems.Concat(ggufItems).ToArray();
    }

    // Appends the two non-node-local families after the node-local ones, in the one order both list paths use: cloud (Codex, Azure) first, then the operator's external
    // connections. One helper rather than two hand-written concat chains, because the available and unavailable paths disagreeing about this order is drift nothing would catch.
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
    ///     declared capability matrix (<see cref="CodexProviderCapabilities.V0" />) rather than an Ollama
    ///     classification, because the local runtime has never seen these ids. Size and quantization stay null: they
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
    ///     provider's declared capability matrix (<see cref="AzureFoundryProviderCapabilities.V0" />) rather than an
    ///     Ollama classification, because the local runtime has never seen these deployment ids. The deployment name is
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

    public static ListLocalModelsResponse ToUnavailableListResponse(string? selectedModelName,
        string? configuredDefaultModelName,
        string error,
        IReadOnlyList<LocalModelResponse>? cloudModels = null,
        IReadOnlyList<LocalModelDescriptor>? ggufModels = null,
        IReadOnlyList<LocalModelResponse>? externalModels = null)
    {
        // Ollama is unavailable, but node-local GGUFs (served by llama.cpp) do not depend on it — surface them so a no-Ollama box can still select and chat over an installed
        // GGUF. A Codex session likewise offers cloud models, and an external connection is served by someone else's endpoint. Order mirrors the success path: GGUF, cloud, external.
        var ggufItems = ggufModels is { Count: > 0 }
            ? ToLlamaCppModelResponses(ggufModels, selectedModelName)
            : [];

        var items = ConcatRemoteModels(ggufItems, cloudModels, externalModels);

        // IsAvailable reflects whether a node-local runtime can serve a chat: true once at least one GGUF is installed (llama.cpp can serve it), even though Ollama itself is
        // down. Cloud-only (no GGUF) keeps the local runtime reported unavailable.
        var isAvailable = ggufItems.Count > 0;

        return new ListLocalModelsResponse
        {
            IsAvailable = isAvailable,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,

            // The unavailability sentence belongs to an unavailable list only: reporting a local runtime that IS available alongside
            // "Local model provider is unavailable." is a contradiction any client reading `error` would render as a false alarm.
            Error = isAvailable ? null : error,
            Items = items
        };
    }

    public static RunningLocalModelsResponse ToRunningResponse(IEnumerable<RunningModelSnapshot> runningModels, bool ollamaConfigured)
    {
        ArgumentNullException.ThrowIfNull(runningModels);

        return new RunningLocalModelsResponse
        {
            IsAvailable = true,
            OllamaConfigured = ollamaConfigured,
            Items = runningModels
                    .Select(static snapshot => (Name: ReadRunningModelName(snapshot), Snapshot: snapshot))
                    .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))

                    // "Running" means resident in this node's RAM/VRAM, and an external model is served by someone else's process, so it can never legitimately appear here:
                    // were a runtime to echo an ext: id back, listing it would invite an eject/unload action against a process this node does not own.
                    .Where(static entry => !ExternalModelId.HasExternalScheme(entry.Name))
                    .Select(static entry => new RunningLocalModelResponse
                    {
                        ModelName = entry.Name,
                        SizeBytes = entry.Snapshot.SizeBytes,
                        SizeVramBytes = entry.Snapshot.SizeVramBytes,
                        ExpiresAtUtc = entry.Snapshot.ExpiresAt?.ToUnixTimeMilliseconds()
                    })
                    .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
        };
    }

    public static RunningLocalModelsResponse ToUnavailableRunningResponse(string error, bool ollamaConfigured)
    {
        return new RunningLocalModelsResponse
        {
            IsAvailable = false,
            OllamaConfigured = ollamaConfigured,
            Error = error,
            Items = []
        };
    }

    public static LocalModelResponse ToResponse(this OllamaModelSummary model,
        string? selectedModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(classifications);

        var modelName = model.Name;
        var classification = classifications.TryGetValue(modelName, out var resolved)
            ? resolved
            : UnknownClassification(modelName);

        return new LocalModelResponse
        {
            ModelName = modelName,
            SizeBytes = model.SizeBytes,
            ModifiedAtUtc = model.ModifiedAtUtc.ToUnixTimeMilliseconds(),
            Family = model.Family,
            ParameterSize = model.ParameterSize,
            QuantizationLevel = model.QuantizationLevel,
            IsSelected = string.Equals(modelName, selectedModelName, StringComparison.OrdinalIgnoreCase),
            Kind = classification.Kind.ToString(),
            DetectedKind = classification.DetectedKind.ToString(),
            Capabilities = classification.Capabilities,
            IsReasoningCapable = ModelKindDetector.SupportsThinking(classification.Capabilities),
            IsToolCapable = ModelKindDetector.SupportsTools(classification.Capabilities),
            IsOverridden = classification.IsOverridden
        };
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

    private static ModelClassificationResult UnknownClassification(string modelName)
    {
        return new ModelClassificationResult { ModelName = modelName, Kind = ModelKind.Unknown, DetectedKind = ModelKind.Unknown, Capabilities = [], IsOverridden = false };
    }

    public static LocalModelDetailsResponse ToResponse(this OllamaModelDetails modelDetails, string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelDetails);

        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = modelDetails.MaxContextTokens,
            Template = modelDetails.Template,
            System = modelDetails.System,
            License = modelDetails.License
        };
    }

    /// <summary>
    ///     Maps an installed GGUF descriptor (served by llama.cpp) to the shared model-details response, keeping its
    ///     shape aligned with the Ollama branch.
    /// </summary>
    /// <remarks>
    ///     <see cref="LocalModelDetailsResponse.MaxContextTokens" /> is the descriptor's advertised train ceiling and
    ///     <paramref name="effectiveContextTokens" /> the RUNNING process's launched context window, when a chat
    ///     process is warm. <c>Template</c>/<c>System</c>/<c>License</c> are Ollama Modelfile concepts a raw GGUF has
    ///     no equivalent of, so they stay null.
    /// </remarks>
    public static LocalModelDetailsResponse ToDetailsResponse(this LocalModelDescriptor descriptor, string modelName, int? effectiveContextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = descriptor.MaxContextTokens,
            EffectiveContextTokens = effectiveContextTokens,
            Origin = descriptor.Origin,
            ModelContentFingerprint = descriptor.ModelContentFingerprint,
            Template = null,
            System = null,
            License = null
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

    private static string ReadRunningModelName(RunningModelSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.ModelName)
            ? snapshot.ModelName
            : snapshot.Name ?? string.Empty;
    }
}
