namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

internal static class LocalModelsMapper
{
    public static ListLocalModelsResponse ToListResponse(IEnumerable<Model> models,
        string? selectedModelName,
        string? configuredDefaultModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications,
        IReadOnlyList<LocalModelResponse>? cloudModels = null,
        IReadOnlyList<LocalModelDescriptor>? ggufModels = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(classifications);

        var ollamaItems = models
                          .Where(static model => !string.IsNullOrWhiteSpace(model.ModelName) || !string.IsNullOrWhiteSpace(model.Name))
                          .Select(model => model.ToResponse(selectedModelName, classifications))
                          .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                          .ToArray();

        // Order: local Ollama → local GGUF (llamacpp) → cloud. GGUF entries are deduped against the Ollama names so a
        // name present under both runtimes is listed once (Ollama wins). The picker groups by Provider, so the families
        // stay visually separated; cloud (Codex) stays last in its catalog (strongest-first) order.
        var localItems = ConcatGgufModels(ollamaItems, ggufModels, selectedModelName);

        var items = cloudModels is { Count: > 0 }
            ? localItems.Concat(cloudModels).ToArray()
            : localItems;

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
    ///     <see cref="LocalModelProviders.LlamaCpp" />. GGUF chat models are classified <see cref="ModelKind.Chat" />
    ///     WITHOUT an <c>/api/show</c> probe — a downloaded GGUF in the chat picker has a completion head by
    ///     construction. Reasoning/tool support and the capability tokens are detected offline from the model's GGUF
    ///     chat template (carried on the descriptor by the store); a model whose template could not be read defaults to
    ///     the safe no-tools/no-reasoning classification (a non-tool model is never offered tools). An embedding-only
    ///     GGUF is recognized offline from its name (<see cref="ModelKindDetector.IsEmbeddingName" />, matching
    ///     EMBED/NOMIC-EMBED/BGE-… fragments) and tagged <see cref="ModelKind.Embedding" />; a reranker
    ///     (cross-encoder) GGUF is recognized from its name (<see cref="ModelKindDetector.IsRerankerName" />, matching
    ///     RERANK, checked FIRST since a name like <c>bge-reranker-…</c> matches the embedding prefix too) and tagged
    ///     <see cref="ModelKind.Reranker" />. Both are filtered out by the React <c>kind === "Chat"</c> picker; every
    ///     other GGUF stays Chat.
    /// </summary>
    public static IReadOnlyList<LocalModelResponse> ToLlamaCppModelResponses(IReadOnlyList<LocalModelDescriptor> ggufModels,
        string? selectedModelName)
    {
        ArgumentNullException.ThrowIfNull(ggufModels);

        return ggufModels
               .Where(static descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName))
               .Select(descriptor =>
               {
                   var kind = ClassifyGgufKindByName(descriptor.ModelName);
                   return new LocalModelResponse
                   {
                       ModelName = descriptor.ModelName,
                       Provider = LocalModelProviders.LlamaCpp,
                       SizeBytes = descriptor.SizeBytes,
                       ModifiedAtUtc = descriptor.ModifiedAt?.ToUnixTimeMilliseconds(),
                       IsSelected = string.Equals(descriptor.ModelName, selectedModelName, StringComparison.OrdinalIgnoreCase),
                       Kind = kind.ToString(),
                       DetectedKind = kind.ToString(),
                       Capabilities = descriptor.Capabilities,
                       IsReasoningCapable = descriptor.IsReasoningCapable,
                       IsToolCapable = descriptor.IsToolCapable,
                       IsOverridden = false
                   };
               })
               .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
               .ToArray();
    }

    /// <summary>
    ///     Classifies an installed GGUF from its name alone (a fresh GGUF carries no capability probe). A
    ///     speculative-decoding drafter is checked FIRST: its key carries the exact <c>MTP-</c> quant marker, and left
    ///     unclassified it would default to Chat and sit in the picker as a 0.4 GB twin of the real model it drafts for.
    ///     Reranker is then checked before embedding because a reranker name such as <c>bge-reranker-…</c> also matches
    ///     the embedding prefix, and the reranker classification is the correct one. Any other name defaults to Chat.
    /// </summary>
    private static ModelKind ClassifyGgufKindByName(string modelName)
    {
        if (ModelKindDetector.IsDraftName(modelName))
        {
            return ModelKind.Draft;
        }

        if (ModelKindDetector.IsRerankerName(modelName))
        {
            return ModelKind.Reranker;
        }

        return ModelKindDetector.IsEmbeddingName(modelName) ? ModelKind.Embedding : ModelKind.Chat;
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

    /// <summary>
    ///     Maps the offered Codex cloud models (<see cref="CodexModelCatalog.ModelIds" />) to model-list entries tagged
    ///     <see cref="LocalModelProviders.CodexOAuth" />. The endpoint passes these only when a Codex session is
    ///     present. Each entry advertises the Codex provider's declared capability matrix
    ///     (<see cref="CodexProviderCapabilities.V0" />) rather than an Ollama classification (the local runtime has
    ///     never seen these ids). Size/quantization fields stay null — they are local-runtime concepts.
    /// </summary>
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
    ///     <see cref="LocalModelProviders.AzureFoundry" />. The endpoint passes these only when an Azure connection is
    ///     stored. Each entry advertises the Azure provider's declared capability matrix
    ///     (<see cref="AzureFoundryProviderCapabilities.V0" />) rather than an Ollama classification (the local runtime
    ///     has never seen these deployment ids). The deployment name is the model id; an optional display label rides
    ///     along. Size/quantization fields stay null — they are local-runtime concepts.
    /// </summary>
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
        IReadOnlyList<LocalModelDescriptor>? ggufModels = null)
    {
        // Ollama is unavailable, but node-local GGUFs (served by llama.cpp) do not depend on it — surface them so a
        // no-Ollama box can still select and chat over an installed GGUF. A present Codex session likewise offers cloud
        // models. Order mirrors the success path: GGUF (local) then cloud.
        var ggufItems = ggufModels is { Count: > 0 }
            ? ToLlamaCppModelResponses(ggufModels, selectedModelName)
            : [];

        var items = cloudModels is { Count: > 0 }
            ? ggufItems.Concat(cloudModels).ToArray()
            : ggufItems;

        return new ListLocalModelsResponse
        {
            // IsAvailable reflects whether a node-local runtime can serve a chat: true once at least one GGUF is
            // installed (llama.cpp can serve it), even though Ollama itself is down. Cloud-only (no GGUF) keeps the
            // local runtime reported unavailable.
            IsAvailable = ggufItems.Count > 0,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Error = error,
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

    public static LocalModelResponse ToResponse(this Model model,
        string? selectedModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(classifications);

        var modelName = model.ReadModelName();
        var classification = classifications.TryGetValue(modelName, out var resolved)
            ? resolved
            : UnknownClassification(modelName);

        return new LocalModelResponse
        {
            ModelName = modelName,
            SizeBytes = model.Size,
            ModifiedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(model.ModifiedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            Family = model.Details?.Family,
            ParameterSize = model.Details?.ParameterSize,
            QuantizationLevel = model.Details?.QuantizationLevel,
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
        return new ModelClassificationResult(modelName, ModelKind.Unknown, ModelKind.Unknown, [], IsOverridden: false);
    }

    public static LocalModelDetailsResponse ToResponse(this OllamaModelDetails modelDetails, string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelDetails);

        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = modelDetails.MaxContextTokens,
            Template = modelDetails.Response.Template,
            System = modelDetails.Response.System,
            License = modelDetails.Response.License
        };
    }

    /// <summary>
    ///     Maps an installed GGUF descriptor (served by llama.cpp) to the shared model-details response.
    ///     <see cref="LocalModelDetailsResponse.MaxContextTokens" /> is the descriptor's advertised train ceiling and
    ///     <paramref name="effectiveContextTokens" /> the RUNNING process's launched context window (AUD4-02), when a
    ///     chat process is warm. <c>Template</c>/<c>System</c>/<c>License</c> are Ollama Modelfile concepts a raw GGUF has
    ///     no equivalent of, so they stay null. Keeps the response shape aligned with the Ollama branch.
    /// </summary>
    public static LocalModelDetailsResponse ToDetailsResponse(this LocalModelDescriptor descriptor, string modelName, int? effectiveContextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new LocalModelDetailsResponse
        {
            ModelName = modelName,
            MaxContextTokens = descriptor.MaxContextTokens,
            EffectiveContextTokens = effectiveContextTokens,
            Template = null,
            System = null,
            License = null
        };
    }

    internal static string ReadModelName(this Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ModelName)
            ? model.ModelName
            : model.Name ?? string.Empty;
    }

    private static string ReadRunningModelName(RunningModelSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(snapshot.ModelName)
            ? snapshot.ModelName
            : snapshot.Name ?? string.Empty;
    }
}
