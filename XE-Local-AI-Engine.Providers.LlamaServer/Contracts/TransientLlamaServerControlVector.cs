namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The control vector a launch applied, as checked against its GGUF before the process started.</summary>
public sealed record TransientLlamaServerControlVector
{
    /// <summary>The <c>N</c> of the file's one <c>direction.N</c> tensor: the block the vector is added at.</summary>
    public required int Layer { get; init; }

    public required long ElementCount { get; init; }

    public required long SizeBytes { get; init; }

    public required string Sha256 { get; init; }

    public required double Scale { get; init; }
}
