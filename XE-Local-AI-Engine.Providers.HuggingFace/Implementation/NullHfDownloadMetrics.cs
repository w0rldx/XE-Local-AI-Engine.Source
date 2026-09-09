namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>No-op <see cref="IHfDownloadMetrics" /> — the default when no metrics sink is wired.</summary>
public sealed class NullHfDownloadMetrics : IHfDownloadMetrics
{
    /// <summary>The shared no-op instance.</summary>
    public static NullHfDownloadMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordReadIdleTimeout()
    {
        // Intentionally does nothing — the null-object default.
    }
}
