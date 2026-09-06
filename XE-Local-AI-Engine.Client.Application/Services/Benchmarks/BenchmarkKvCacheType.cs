namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The KV-cache types a benchmark run may be launched with, and the single place that turns a requested type into
///     llama-server launch arguments.
/// </summary>
/// <remarks>
///     <para>
///         The allow-list is <c>f16 | q8_0 | q4_0</c>, symmetric by construction (K == V). "Auto" is the absence of a
///         request — <see langword="null" /> on the wire — and is resolved to a concrete type at freeze, never here.
///     </para>
///     <para>
///         <see cref="Apply" /> encodes the flag rule: <c>f16</c> emits no <c>-ctk/-ctv</c> and leaves flash attention
///         at the runtime's own default; a quantized type sets both cache types and requires <c>-fa on</c>. Frozen
///         placement (<c>-c/-ngl/-ts/-ot</c>) is carried through untouched, so changing the KV type never re-fits the
///         run.
///     </para>
/// </remarks>
public static class BenchmarkKvCacheType
{
    /// <inheritdoc cref="LlamaServerKvCacheTypes.F16" />
    public const string F16 = LlamaServerKvCacheTypes.F16;

    /// <inheritdoc cref="LlamaServerKvCacheTypes.Q8_0" />
    public const string Q8_0 = LlamaServerKvCacheTypes.Q8_0;

    /// <inheritdoc cref="LlamaServerKvCacheTypes.Q4_0" />
    public const string Q4_0 = LlamaServerKvCacheTypes.Q4_0;

    /// <summary>The run asked for this exact type.</summary>
    public const string SourceExplicit = "explicit";

    /// <summary>The run asked for Auto and freeze picked the type.</summary>
    public const string SourceAuto = "auto";

    /// <summary><see langword="true" /> when the canonical <paramref name="type" /> needs <c>-ctk/-ctv</c> + <c>-fa on</c>.</summary>
    public static bool IsQuantized(string type) =>
        LlamaServerKvCacheTypes.IsQuantized(type);

    /// <summary>
    ///     Canonicalizes a requested type: trimmed and lowercased, against the shared allow-list in
    ///     <see cref="LlamaServerKvCacheTypes" />. A missing/blank request is Auto (<paramref name="normalized" /> is
    ///     <see langword="null" />) and is valid — that resolution belongs to THIS caller, not to the shared authority,
    ///     which answers validity only; an unrecognized value returns <see langword="false" /> so the endpoint can
    ///     answer 400.
    /// </summary>
    public static bool TryNormalize(string? requested, out string? normalized) =>
        LlamaServerKvCacheTypes.TryNormalize(requested, out normalized);

    /// <summary>
    ///     The frozen replay re-expressed with <paramref name="effective" /> as its KV-cache type, keeping the frozen
    ///     placement. <paramref name="effective" /> must already be canonical (see <see cref="TryNormalize" />).
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="effective" /> is not an allowed type, or <paramref name="frozenBase" /> is an explore-mode
    ///     resolution (a benchmark always launches from a replay).
    /// </exception>
    public static ResolvedLaunchArguments Apply(ResolvedLaunchArguments frozenBase, string effective)
    {
        ArgumentNullException.ThrowIfNull(frozenBase);
        if (!TryNormalize(effective, out var normalized) || normalized is null)
        {
            throw new ArgumentException($"'{effective}' is not a supported benchmark KV-cache type.", nameof(effective));
        }

        if (frozenBase.ExploreMode)
        {
            throw new ArgumentException("A benchmark KV-cache type can only be applied to a replay resolution.", nameof(frozenBase));
        }

        return IsQuantized(normalized)
            ? ResolvedLaunchArguments.Replay(frozenBase.CtxSize,
                frozenBase.NGpuLayers,
                frozenBase.TensorSplit,
                frozenBase.OverrideTensor,
                normalized,
                normalized,
                flashAttn: true)
            : frozenBase.WithoutKvCacheQuantization();
    }
}

/// <summary>
///     The requested KV-cache type cannot be launched on the frozen runtime — a quantized type on a CPU build, or a
///     type the selected binary's capability manifest does not advertise. Mapped to 422 <c>UnsupportedKvCacheType</c>.
/// </summary>
public sealed class BenchmarkUnsupportedKvCacheTypeException(string message) : InvalidOperationException(message);
