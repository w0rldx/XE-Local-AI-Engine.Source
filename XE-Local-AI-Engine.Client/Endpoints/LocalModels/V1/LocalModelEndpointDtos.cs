namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

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
