namespace XE_Local_AI_Engine.Providers.Ollama;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Maps OllamaSharp's <see cref="RunningModel" /> (the <c>/api/ps</c> payload) into the provider-neutral
///     <see cref="RunningModelSnapshot" />. Shared by the two surfaces that surface running models — the
///     app-service <c>OllamaModelService</c> (consumed by the running-models endpoint) and the provider-neutral
///     <c>OllamaModelCapabilityClient</c> (consumed by the capability prober) — so the size/expiry normalization
///     lives in exactly one place.
/// </summary>
public static class RunningModelSnapshotMapper
{
    /// <summary>Projects a single Ollama running model into a sanitized <see cref="RunningModelSnapshot" />.</summary>
    public static RunningModelSnapshot ToSnapshot(RunningModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new RunningModelSnapshot(
            model.Name,
            model.ModelName,
            NormalizeExpiresAt(model.ExpiresAt),
            NormalizeNonNegative(model.Size),
            NormalizeNonNegative(model.SizeVram));
    }

    // Ollama reports size/size_vram as raw byte counts. A zero or negative value means "not reported"; surface those as
    // null so the UI can omit the memory column rather than render a misleading 0 B footprint.
    private static long? NormalizeNonNegative(long value)
    {
        return value > 0 ? value : null;
    }

    // A running model's expiry is a UTC instant; the default DateTime means the runtime did not report one, so surface
    // null rather than the .NET epoch. The kind is forced to UTC because Ollama reports the eviction time in UTC.
    private static DateTimeOffset? NormalizeExpiresAt(DateTime expiresAt)
    {
        return expiresAt == default
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc));
    }
}
