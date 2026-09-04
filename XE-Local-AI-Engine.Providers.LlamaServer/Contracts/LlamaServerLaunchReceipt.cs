namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Runtime.InteropServices;

/// <summary>
///     Whether a benchmark spawn loaded anything beside the base weights. Recorded as flags, never as paths or digests
///     — the presence of an adapter/projector/draft model is the fact a later reader needs; which file it was is not.
/// </summary>
/// <param name="HasLora">A LoRA adapter was applied on top of the base model (<c>--lora</c>).</param>
/// <param name="HasMmproj">A vision projector companion was loaded (<c>--mmproj</c>).</param>
/// <param name="HasDraft">An external speculative draft model was loaded (<c>--spec-draft-model</c>).</param>
public readonly record struct LlamaServerLaunchAuxAssets(bool HasLora, bool HasMmproj, bool HasDraft);

/// <summary>
///     Measured layer placement for one spawn: the class plus the raw counts it was derived from, so a reader can see
///     <c>38/49</c> rather than only "partial".
/// </summary>
/// <param name="Outcome">The placement class measured from llama.cpp's load banner.</param>
/// <param name="OffloadedLayers">Layers placed on the GPU, or <see langword="null" /> when no banner was observed.</param>
/// <param name="TotalLayers">Total model layers, or <see langword="null" /> when no banner was observed.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LlamaServerLaunchPlacement(
    LlamaServerPlacementOutcome Outcome,
    int? OffloadedLayers,
    int? TotalLayers);

/// <summary>
///     What a benchmark spawn ACTUALLY launched, captured once the process reached readiness. It is evidence, not a
///     verdict: every member is a raw observed fact, and nothing here declares two runs comparable.
/// </summary>
/// <remarks>
///     <para>
///         Assembled only for a benchmark spawn (the one spawn shape that is a measurement rather than serving), and
///         only after readiness, so a process that never served records nothing. Assembly is non-throwing: a fact that
///         cannot be read is recorded as <see langword="null" /> rather than failing the run.
///     </para>
///     <para>
///         <strong>Nothing addressable appears here</strong> — no model or executable path, no host, no port. The
///         executable is identified by digest and version only.
///     </para>
/// </remarks>
/// <param name="ReceiptVersion">Schema version of this receipt; <see cref="LlamaServerLaunchReceipt.CurrentVersion" /> is what a spawn on this build writes.</param>
/// <param name="Variant">The llama.cpp build the spawn ran on.</param>
/// <param name="Os">Host operating system token (<c>linux</c>/<c>windows</c>/<c>macos</c>/<c>unknown</c>).</param>
/// <param name="ExecutableVersion">The llama.cpp release the executable reported, or <see langword="null" />.</param>
/// <param name="ExecutableSha256">
///     Lowercase SHA-256 of the image the launched process is RUNNING, read back from the live process rather than from
///     the path the launch resolved. <see langword="null" /> when the running image could not be read.
/// </param>
/// <param name="ManifestSha256">
///     The digest the capability probe recorded for the executable it inspected. A mismatch against
///     <see cref="ExecutableSha256" /> means the binary changed between capability probe and launch.
/// </param>
/// <param name="LaunchProjection">
///     The allow-listed launch shape this spawn EMITTED, read back from the final argument vector the process was
///     started with (<see cref="LlamaServerLaunchProjection.TryFromArguments" />), falling back to the intended
///     projection only when that vector could not be parsed. The intended shape is the
///     <see cref="LlamaServerLaunchProjection.From" /> identity a caller computes before the spawn; when the two
///     identities differ, <see cref="OmittedOptions" /> usually says why.
/// </param>
/// <param name="AuxAssets">Whether anything beyond the base weights was loaded.</param>
/// <param name="Placement">Measured layer placement plus its raw counts.</param>
/// <param name="EffectiveContextTokens">
///     The per-slot context the server reported after loading, or <see langword="null" /> when <c>/props</c> was
///     unavailable. This is the window the model actually got, which a clamp can make smaller than the requested one.
/// </param>
/// <param name="BenchmarkLaunchPolicy">The frozen benchmark-only server settings this spawn ran under.</param>
public sealed record LlamaServerLaunchReceipt(
    int ReceiptVersion,
    GpuVariant Variant,
    string Os,
    string? ExecutableVersion,
    string? ExecutableSha256,
    string? ManifestSha256,
    LlamaServerLaunchProjection LaunchProjection,
    LlamaServerLaunchAuxAssets AuxAssets,
    LlamaServerLaunchPlacement Placement,
    int? EffectiveContextTokens,
    LlamaServerBenchmarkLaunchPolicy BenchmarkLaunchPolicy)
{
    /// <summary>The schema version every receipt this build produces carries.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    ///     The optional launch options the capability gate REMOVED because the selected runtime does not advertise them
    ///     — <c>--cache-reuse</c>, <c>--metrics</c>, <c>-lv</c>. A missing KV-cache or flash-attention option is never in
    ///     here: the gate refuses that launch outright rather than dropping the flag. Empty when nothing was omitted,
    ///     which is the ordinary case; a non-empty list is the fact that explains an intended-versus-emitted difference
    ///     instead of leaving it unexplained.
    /// </summary>
    public IReadOnlyList<string> OmittedOptions { get; init; } = [];
}
