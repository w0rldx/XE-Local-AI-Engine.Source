namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>Builds the scrubbed server-side support bundle the Diagnostics panel merges with its browser snapshot.</summary>
public interface ISupportBundleService
{
    /// <param name="isShellOwned">Passed through to <see cref="INodeInfoService.GetAsync" />.</param>
    /// <param name="ct">The request's cancellation.</param>
    Task<SupportBundle> BuildAsync(bool isShellOwned, CancellationToken ct);
}

/// <summary>A built support bundle: the zip bytes and the download file name.</summary>
public sealed record SupportBundle
{
    public required ReadOnlyMemory<byte> Zip { get; init; }

    public required string FileName { get; init; }
}
