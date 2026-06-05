namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

internal static class LocalModelsMapper
{
    public static ListLocalModelsResponse ToListResponse(IEnumerable<Model> models,
        string? selectedModelName,
        string? configuredDefaultModelName,
        IReadOnlyDictionary<string, ModelClassificationResult> classifications)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(classifications);

        return new ListLocalModelsResponse
        {
            IsAvailable = true,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Items = models
                    .Where(static model => !string.IsNullOrWhiteSpace(model.ModelName) || !string.IsNullOrWhiteSpace(model.Name))
                    .Select(model => model.ToResponse(selectedModelName, classifications))
                    .OrderBy(static model => model.ModelName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
        };
    }

    public static ListLocalModelsResponse ToUnavailableListResponse(string? selectedModelName,
        string? configuredDefaultModelName,
        string error)
    {
        return new ListLocalModelsResponse
        {
            IsAvailable = false,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
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

    internal static string ReadModelName(this Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ModelName)
            ? model.ModelName
            : model.Name ?? string.Empty;
    }
}
