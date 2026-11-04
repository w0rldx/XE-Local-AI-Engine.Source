namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Estimates the resident memory footprint (bytes) of an INSTALLED local GGUF model against a hardware profile, wrapping the pure
///     <c>MemoryFitEstimator</c>.
/// </summary>
/// <remarks>
///     The quant label and on-disk size come from the GGUF registry, the weight and KV header inputs from a single tolerant header read, both
///     via <c>IGgufModelStore</c>. The result distinguishes a Known byte estimate from Unknown — the model is not installed, or there is no
///     header AND no file size — so the capacity gate can reject conservatively on uncertainty.
/// </remarks>
public interface IModelFootprintProvider
{
    /// <summary>
    ///     Resolves the estimated footprint of the installed model <paramref name="modelName" /> against
    ///     <paramref name="profile" />. Never throws for an absent/unreadable model (cancellation excepted).
    /// </summary>
    /// <remarks>
    ///     <see cref="ModelFootprint.NotInstalled" /> when the model is not installed; <see cref="ModelFootprint.Unknown" /> when it
    ///     carries neither header metadata nor a usable file size.
    /// </remarks>
    Task<ModelFootprint> ResolveFootprintAsync(string modelName, ModelRole role, HardwareProfile profile, CancellationToken ct);

    /// <summary>
    ///     Resolves a footprint that can satisfy <paramref name="requiredContextTokens" />, with the KV-cache term sized for
    ///     <paramref name="kvCacheType" />.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" />, f16 or an unrecognized value takes the conservative fp16 sizing every other caller gets. The default
    ///     implementation preserves source compatibility for providers that do not need a context-specific path.
    /// </remarks>
    Task<ModelFootprint> ResolveFootprintAsync(string modelName,
        ModelRole role,
        HardwareProfile profile,
        int? requiredContextTokens,
        string? kvCacheType,
        CancellationToken ct) =>
        ResolveFootprintAsync(modelName, role, profile, ct);

    /// <summary>
    /// Purely recomputes the next smaller automatic hardware-tier footprint candidate for admission. Frozen and
    /// deterministic allocations cannot be adjusted; shared launch state is never changed by it.
    /// </summary>
    bool TryDownTierForAdmission(ModelFootprint current, out ModelFootprint downTiered);

    /// <summary>
    /// Validates a fitting admission candidate and returns the footprint that launch will consume: the candidate, or a
    /// lower out-of-memory down-tier already in force. A caller reserves the returned footprint, not the proposed one.
    /// </summary>
    bool TryCommitAdmissionFootprint(ModelFootprint candidate, out ModelFootprint committed);
}

/// <summary>
///     The footprint of an installed model: <see cref="IsKnown" /> with <see cref="Resources" /> when the estimator produced a figure, or
///     Unknown.
/// </summary>
/// <remarks>
///     Not known means the model is not installed, the caller's required context exceeds what the process allocates, or neither header
///     metadata nor a file size was available. The capacity gate treats each as a reject, with its own reason.
/// </remarks>
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

    /// <summary>Whether there is no estimate because the model is not installed: no registry entry, or its file is gone.</summary>
    public bool IsNotInstalled { get; private init; }

    /// <summary>Whether there is no estimate because the caller's required context exceeds what the model's process allocates.</summary>
    public bool ExceedsProcessContext { get; private init; }

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

    /// <summary>An undeterminable footprint of an installed model: no header metadata and no file size.</summary>
    public static ModelFootprint Unknown { get; } = new(isKnown: false, ResourceFootprint.Zero, admission: null);

    /// <summary>The model is not installed on this node.</summary>
    public static ModelFootprint NotInstalled { get; } = new(isKnown: false, ResourceFootprint.Zero, admission: null)
    {
        IsNotInstalled = true
    };

    /// <summary>The caller's required context exceeds what the model's process allocates.</summary>
    public static ModelFootprint ContextTooLarge { get; } = new(isKnown: false, ResourceFootprint.Zero, admission: null)
    {
        ExceedsProcessContext = true
    };
}
