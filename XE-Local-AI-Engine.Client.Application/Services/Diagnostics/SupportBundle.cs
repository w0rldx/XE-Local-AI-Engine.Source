namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>A built support bundle: the zip bytes and the download file name.</summary>
public sealed record SupportBundle
{
    public required ReadOnlyMemory<byte> Zip { get; init; }

    public required string FileName { get; init; }
}
