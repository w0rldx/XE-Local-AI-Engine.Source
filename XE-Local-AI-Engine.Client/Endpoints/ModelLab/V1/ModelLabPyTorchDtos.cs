namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Request DTO for building a model's PyTorch index. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class BuildModelLabPyTorchIndexRequest
{
    public string? ModelName { get; init; }

    /// <summary><c>checkpoint</c>: the config of a downloaded checkpoint, by <see cref="BaseArtifactId" />. <c>huggingface</c>: only the config of <see cref="RepoId" />.</summary>
    public string? Source { get; init; }

    public Guid? BaseArtifactId { get; init; }

    public string? RepoId { get; init; }

    /// <summary>Optional commit or branch for <c>huggingface</c>. The index names the commit it resolved to.</summary>
    public string? Revision { get; init; }
}

/// <summary>Request DTO for one source file of an index, bound from the query.</summary>
public sealed class GetModelLabPyTorchSourceRequest
{
    /// <summary>A file as the index names it: relative to transformers, or prefixed <c>torch:</c>.</summary>
    public string? File { get; init; }

    /// <summary>The index's <c>indexId</c>.</summary>
    public string? Index { get; init; }
}

/// <summary>An index or a source file refused. <see cref="Reason" /> is a stable code; the message is what to do next.</summary>
public sealed class ModelLabPyTorchBlockedResponse
{
    public required ModelLabPyTorchRefusal Reason { get; init; }

    public required string Message { get; init; }
}
