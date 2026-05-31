namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

/// <summary>
///     Response DTO for list local models operations.
/// </summary>
public sealed class ListLocalModelsResponse
{
    public required bool IsAvailable { get; init; }

    public string? SelectedModelName { get; init; }

    public string? ConfiguredDefaultModelName { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<LocalModelResponse> Items { get; init; }
}

/// <summary>
///     Request DTO for get local model details operations.
/// </summary>
public sealed class GetLocalModelDetailsRequest
{
    public string? ModelName { get; init; }
}

/// <summary>
///     Request DTO for delete local model operations.
/// </summary>
public sealed class DeleteLocalModelRequest
{
    public string? ModelName { get; init; }
}

/// <summary>
///     Request DTO for select local model operations.
/// </summary>
public sealed class SelectLocalModelRequest
{
    public string? ModelName { get; init; }
}

/// <summary>
///     Request DTO for pull local model operations.
/// </summary>
public sealed class PullLocalModelRequest
{
    public string? ModelName { get; init; }
}

/// <summary>
///     Response DTO for local model operations.
/// </summary>
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

/// <summary>
///     Response DTO for local model details operations.
/// </summary>
public sealed class LocalModelDetailsResponse
{
    public required string ModelName { get; init; }

    public int? MaxContextTokens { get; init; }

    public string? Template { get; init; }

    public string? System { get; init; }

    public string? License { get; init; }
}

/// <summary>
///     Response DTO for select local model operations.
/// </summary>
public sealed class SelectLocalModelResponse
{
    public required string SelectedModelName { get; init; }
}

/// <summary>
///     Response DTO for pull local model operations.
/// </summary>
public sealed class PullLocalModelResponse
{
    public required string ModelName { get; init; }

    public required string Status { get; init; }

    public long? TotalBytes { get; init; }

    public long? CompletedBytes { get; init; }
}

/// <summary>
///     Response DTO for delete local model operations.
/// </summary>
public sealed class DeleteLocalModelResponse
{
    public required string ModelName { get; init; }

    public required bool Deleted { get; init; }
}
