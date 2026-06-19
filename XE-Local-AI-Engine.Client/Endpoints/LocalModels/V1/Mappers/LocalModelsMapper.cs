namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.CodexOAuth;

internal static class LocalModelsMapper
{
    public static ListLocalModelsResponse ToListResponse(IEnumerable<Model> models,
        string? selectedModelName,
        string? configuredDefaultModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications,
        IReadOnlyList<LocalModelResponse>? cloudModels = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(classifications);

        var localItems = models
                         .Where(static model => !string.IsNullOrWhiteSpace(model.ModelName) || !string.IsNullOrWhiteSpace(model.Name))
                         .Select(model => model.ToResponse(selectedModelName, classifications))
                         .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase);

        // Cloud (Codex) models are appended AFTER the sorted local models as a distinct group, in their catalog
        // (strongest-first) order — the picker groups by Provider, so the two families stay separated in the UI.
        var items = cloudModels is { Count: > 0 }
            ? localItems.Concat(cloudModels).ToArray()
            : localItems.ToArray();

        return new ListLocalModelsResponse
        {
            IsAvailable = true,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Items = items
        };
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

    public static ListLocalModelsResponse ToUnavailableListResponse(string? selectedModelName,
        string? configuredDefaultModelName,
        string error,
        IReadOnlyList<LocalModelResponse>? cloudModels = null)
    {
        return new ListLocalModelsResponse
        {
            // The LOCAL provider is unavailable, but a present Codex session still offers cloud models — surface
            // them so the operator can chat over Codex even when Ollama is down. IsAvailable reflects the local
            // runtime; Items carries any cloud models.
            IsAvailable = false,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Error = error,
            Items = cloudModels is { Count: > 0 } ? cloudModels : []
        };
    }

    public static RunningLocalModelsResponse ToRunningResponse(IEnumerable<RunningModelSnapshot> runningModels)
    {
        ArgumentNullException.ThrowIfNull(runningModels);

        return new RunningLocalModelsResponse
        {
            IsAvailable = true,
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

    public static RunningLocalModelsResponse ToUnavailableRunningResponse(string error)
    {
        return new RunningLocalModelsResponse
        {
            IsAvailable = false,
            Error = error,
            Items = []
        };
    }

    public static PullLocalModelResponse ToPullResponse(string modelName,
        string status,
        long? totalBytes,
        long? completedBytes)
    {
        return new PullLocalModelResponse
        {
            ModelName = modelName,
            Status = status,
            TotalBytes = totalBytes,
            CompletedBytes = completedBytes
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
        return new ModelClassificationResult(modelName, ModelKind.Unknown, ModelKind.Unknown, [], false);
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
