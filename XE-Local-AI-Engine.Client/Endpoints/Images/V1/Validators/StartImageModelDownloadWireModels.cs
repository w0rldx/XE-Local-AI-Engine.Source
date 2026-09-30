namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using XE_Local_AI_Engine.Providers.Abstractions.Image;

internal sealed class StartImageModelDownloadWireValidationResult
{
    public required StartImageModelDownloadWireValues? Values { get; init; }

    public required string? Error { get; init; }

    public bool IsValid => Values is not null;
}

internal sealed class StartImageModelDownloadWireValues
{
    public required string ModelName { get; init; }

    public required string RepoId { get; init; }

    public required ImageModelFamily Family { get; init; }

    public required ImageModelKind Kind { get; init; }

    public required string? Revision { get; init; }

    public required IReadOnlyList<StartImageModelDownloadPartWireValues> Parts { get; init; }
}

internal sealed class StartImageModelDownloadPartWireValues
{
    public required ImageModelPartRole Role { get; init; }

    public required string FileName { get; init; }

    public required string? Sha256 { get; init; }

    public required string? RepoId { get; init; }

    public required long? SizeBytes { get; init; }
}
