namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Estimates the resident memory footprint (bytes) of an INSTALLED local GGUF model against a hardware profile,
///     wrapping the pure <c>MemoryFitEstimator</c>. The quant label and on-disk size come from the GGUF registry; the
///     weight/KV header inputs from a single tolerant header read (both via <c>IGgufModelStore</c>). The result
///     distinguishes a Known byte estimate from Unknown (the model is not installed, or no header AND no file size) so
///     the capacity gate can reject conservatively on uncertainty.
/// </summary>
public interface IModelFootprintProvider
{
    /// <summary>
    ///     Resolves the estimated footprint of the installed model <paramref name="modelName" /> against
    ///     <paramref name="profile" />. Returns <see cref="ModelFootprint.Unknown" /> when the model is not installed or
    ///     carries neither header metadata nor a usable file size. Never throws for an absent/unreadable model
    ///     (cancellation excepted).
    /// </summary>
    Task<ModelFootprint> ResolveFootprintAsync(string modelName, ModelRole role, HardwareProfile profile, CancellationToken ct);

    /// <summary>
    ///     Resolves a footprint that can satisfy <paramref name="requiredContextTokens" />. The default preserves
    ///     source compatibility for providers that do not need a context-specific path.
    /// </summary>
    Task<ModelFootprint> ResolveFootprintAsync(string modelName,
        ModelRole role,
        HardwareProfile profile,
        int? requiredContextTokens,
        CancellationToken ct) => ResolveFootprintAsync(modelName, role, profile, ct);

    /// <summary>
    /// Purely recomputes the next smaller automatic hardware-tier footprint candidate for admission. Frozen and
    /// deterministic allocations cannot be adjusted; shared launch state is unchanged until the explicit commit.
    /// </summary>
    bool TryDownTierForAdmission(ModelFootprint current, out ModelFootprint downTiered);

    /// <summary>
    /// Commits a fitting admission candidate and returns the effective monotone allocation footprint that launch will
    /// consume. A caller must reserve the returned footprint, not the proposed one.
    /// </summary>
    bool TryCommitAdmissionFootprint(ModelFootprint candidate, out ModelFootprint committed);
}

/// <summary>
///     The footprint of an installed model: <see cref="IsKnown" /> with <see cref="Resources" /> when the estimator
///     produced a figure, or Unknown (the model is not installed, or no header metadata and no file size were available).
///     The capacity gate treats Unknown as a reject (invariant: conservative on uncertainty).
/// </summary>
public sealed record ModelFootprint
{
    private ModelFootprint(bool isKnown, ResourceFootprint resources, ProcessLaunchAdmission? admission)
    {
        IsKnown = isKnown;
        Resources = resources;
        Admission = admission;
    }

    /// <summary>Whether a byte estimate is available. <see langword="false" /> ⇒ the gate rejects.</summary>
    public bool IsKnown { get; }

    /// <summary>The estimated resident bytes when <see cref="IsKnown" />; otherwise <c>0</c> (do not consume it).</summary>
    public ResourceFootprint Resources { get; }

    internal ProcessLaunchAdmission? Admission { get; }

    /// <summary>A known dual-axis resource footprint.</summary>
    public static ModelFootprint Known(ResourceFootprint resources)
    {
        if (resources.GpuBytes < 0 || resources.RamBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resources), "Resource axes must be non-negative.");
        }

        return new ModelFootprint(isKnown: true, resources, admission: null);
    }

    internal static ModelFootprint Known(ProcessLaunchAdmission admission) =>
        new(isKnown: true, admission.Allocation.Footprint, admission);

    /// <summary>An undeterminable footprint (not installed, or no header metadata and no file size).</summary>
    public static ModelFootprint Unknown { get; } = new(isKnown: false, ResourceFootprint.Zero, admission: null);
}
