namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed class ListLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<LocalModelResponse> Items { get; init; }
}

public sealed class GetLocalModelDetailsRequest
{
    public string? ModelName { get; init; }
}

public sealed class DeleteLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class SelectLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class PullLocalModelRequest
{
    public string? ModelName { get; init; }
}

public sealed class LocalModelResponse
{
    public required string ModelName { get; init; }

    public long? SizeBytes { get; init; }

    public long? ModifiedAtUtc { get; init; }

    public string? Family { get; init; }

    public string? ParameterSize { get; init; }

    public string? QuantizationLevel { get; init; }

    public required bool IsSelected { get; init; }
}

public sealed class LocalModelDetailsResponse
{
    public required string ModelName { get; init; }

    public int? MaxContextTokens { get; init; }

    public string? Template { get; init; }

    public string? System { get; init; }

    public string? License { get; init; }
}

public sealed class SelectLocalModelResponse
{
    public required string SelectedModelName { get; init; }
}

public sealed class PullLocalModelResponse
{
    public required string ModelName { get; init; }

    public required string Status { get; init; }

    public long? TotalBytes { get; init; }

    public long? CompletedBytes { get; init; }
}

public sealed class DeleteLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Deleted { get; init; }
}

internal static class LocalModelEndpointDtoMapper
{
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
