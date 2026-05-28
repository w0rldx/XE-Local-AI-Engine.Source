namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Services.Chat;

internal static class LocalModelsMapper
{
    public static ListLocalModelsResponse ToListResponse(IEnumerable<Model> models,
        string? selectedModelName,
        string? configuredDefaultModelName)
    {
        ArgumentNullException.ThrowIfNull(models);

        return new ListLocalModelsResponse
        {
            IsAvailable = true,
            SelectedModelName = selectedModelName,
            ConfiguredDefaultModelName = configuredDefaultModelName,
            Items = models
                    .Where(static model => !string.IsNullOrWhiteSpace(model.ModelName) || !string.IsNullOrWhiteSpace(model.Name))
                    .Select(model => model.ToResponse(selectedModelName))
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

    public static LocalModelResponse ToResponse(this Model model, string? selectedModelName)
    {
        ArgumentNullException.ThrowIfNull(model);

        var modelName = model.ReadModelName();
        return new LocalModelResponse
        {
            ModelName = modelName,
            SizeBytes = model.Size,
            ModifiedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(model.ModifiedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
            Family = model.Details?.Family,
            ParameterSize = model.Details?.ParameterSize,
            QuantizationLevel = model.Details?.QuantizationLevel,
            IsSelected = string.Equals(modelName, selectedModelName, StringComparison.OrdinalIgnoreCase)
        };
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

    private static string ReadModelName(this Model model)
    {
        return !string.IsNullOrWhiteSpace(model.ModelName)
            ? model.ModelName
            : model.Name ?? string.Empty;
    }
}
