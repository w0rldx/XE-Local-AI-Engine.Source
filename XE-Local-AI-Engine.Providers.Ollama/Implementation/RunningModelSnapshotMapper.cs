namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Maps OllamaSharp's <see cref="RunningModel" />, the <c>/api/ps</c> payload, into the provider-neutral
///     <see cref="RunningModelSnapshot" />.
/// </summary>
/// <remarks>
///     Shared by the two surfaces that report running models — <c>OllamaModelService</c> behind the running-models
///     endpoint and <c>OllamaModelCapabilityClient</c> behind the capability prober — so the size and expiry
///     normalization lives in exactly one place.
/// </remarks>
public static class RunningModelSnapshotMapper
{
    /// <summary>Projects a single Ollama running model into a sanitized <see cref="RunningModelSnapshot" />.</summary>
    public static RunningModelSnapshot ToSnapshot(RunningModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new RunningModelSnapshot
        {
            Name = model.Name,
            ModelName = model.ModelName,
            ExpiresAt = NormalizeExpiresAt(model.ExpiresAt),
            SizeBytes = NormalizeNonNegative(model.Size),
            SizeVramBytes = NormalizeNonNegative(model.SizeVram)
        };
    }

    // Ollama reports size/size_vram as raw byte counts. A zero or negative value means "not reported"; surface those as
    // null so the UI can omit the memory column rather than render a misleading 0 B footprint.
    private static long? NormalizeNonNegative(long value)
    {
        return value > 0 ? value : null;
    }

    // The default DateTime means no expiry was reported, so surface null rather than the .NET epoch. STJ yields
    // Kind==Local on a non-UTC host, where SpecifyKind stamps the wrong instant; ToUniversalTime preserves it.
    private static DateTimeOffset? NormalizeExpiresAt(DateTime expiresAt)
    {
        return expiresAt == default
            ? null
            : new DateTimeOffset(expiresAt.ToUniversalTime());
    }
}
