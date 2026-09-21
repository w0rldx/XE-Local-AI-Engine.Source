namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>What a <c>--spec-type</c> mode needs to launch, and which draft flags it may emit.</summary>
/// <remarks>
///     The <c>draft-</c> name prefix spans two classes and is NOT a capability test: every allowed mode is mapped to a
///     class explicitly in <see cref="SpeculativeDecodingSettings" />, so adding a mode forces that choice rather than
///     inheriting behaviour from its name.
/// </remarks>
public enum SpeculativeModeClass
{
    /// <summary>Speculation off (<c>none</c>) — no <c>--spec-*</c> flag is emitted at all.</summary>
    Disabled,

    /// <summary>
    ///     Runs a SECOND GGUF as the drafter (<c>draft-simple</c>, <c>draft-eagle3</c>, <c>draft-dflash</c>,
    ///     <c>draft-dspark</c>), costing that model's weights and KV on top of the target.
    /// </summary>
    /// <remarks>
    ///     Requires a draft model path and emits <c>--spec-draft-model</c> plus the draft-model offload knob
    ///     <c>--spec-draft-ngl</c>.
    /// </remarks>
    ExternalDraft,

    /// <summary>
    ///     Drafts from multi-token-prediction heads inside the MAIN model GGUF (<c>draft-mtp</c>): no second model
    ///     exists, so no draft-model flag may be emitted.
    /// </summary>
    /// <remarks>
    ///     Not free for that: the MTP draft context is built over the target model, so it still costs extra context and
    ///     compute VRAM — just not a second set of weights. See docs/wiki/03-local-runtime-and-providers.md, "Per-role
    ///     launch flags and the pooled batch-size rule".
    /// </remarks>
    MainModelHeads,

    /// <summary>
    ///     Self-speculates from the prompt/context (<c>ngram-*</c>): no draft weights, no extra VRAM, and only
    ///     <c>--spec-type</c> is emitted (the drafting knobs are the mode-specific <c>--spec-ngram-*</c> flags).
    /// </summary>
    Draftless
}

/// <summary>
///     Immutable, validated view of the chat-role speculative-decoding launch settings the supervisor turns into
///     <c>--spec-*</c> flags.
/// </summary>
/// <remarks>
///     Speculative decoding drafts several tokens cheaply and verifies them in one target pass, raising single-user
///     throughput; chat role only, since an embedding server does one-shot forward passes with nothing to draft. Of the
///     three capability classes (<see cref="SpeculativeModeClass" />) only
///     <see cref="SpeculativeModeClass.ExternalDraft" /> involves a second GGUF. Like the chat cache-reuse window these
///     are launch flags outside the frozen inference profile's identity, effective on the next natural (re)spawn.
/// </remarks>
/// <param name="Mode">Raw <c>--spec-type</c> value from config; <c>null</c>/empty/<c>none</c> disables.</param>
/// <param name="DraftModelPath">Path to the draft GGUF; required by external-draft modes, ignored by every other class.</param>
/// <param name="DraftMaxTokens">Draft tokens per step (<c>--spec-draft-n-max</c>, upstream default 3); <c>0</c> omits the flag.</param>
/// <param name="DraftGpuLayers">Draft-model GPU offload (<c>--spec-draft-ngl</c>); <c>null</c> omits the flag.</param>
public readonly record struct SpeculativeDecodingSettings(
    string? Mode,
    string? DraftModelPath,
    int DraftMaxTokens,
    int? DraftGpuLayers)
{
    /// <summary>The <c>--spec-type</c> value that disables speculative decoding, and the omit-everything default.</summary>
    public const string DisabledMode = "none";

    /// <summary>
    ///     The <c>--spec-type</c> values this application exposes, each mapped to its capability class; kept lowercase,
    ///     and <see cref="NormalizedMode" /> matches operator input case-insensitively and resolves to these keys.
    /// </summary>
    /// <remarks>
    ///     The exposed set equals the pinned llama-server build's accepted set exactly, each value verified against its
    ///     <c>--help</c>. Why <c>draft-dflash</c>/<c>draft-dspark</c> need no mode-specific field, and why their
    ///     <c>--spec-draft-n-max</c> has to be raised by hand: docs/wiki/03-local-runtime-and-providers.md, "Per-role
    ///     launch flags and the pooled batch-size rule".
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, SpeculativeModeClass> ModeClasses =
        new Dictionary<string, SpeculativeModeClass>(StringComparer.Ordinal)
        {
            [DisabledMode] = SpeculativeModeClass.Disabled,
            ["draft-simple"] = SpeculativeModeClass.ExternalDraft,
            ["draft-eagle3"] = SpeculativeModeClass.ExternalDraft,
            ["draft-dflash"] = SpeculativeModeClass.ExternalDraft,
            ["draft-dspark"] = SpeculativeModeClass.ExternalDraft,
            ["draft-mtp"] = SpeculativeModeClass.MainModelHeads,
            ["ngram-simple"] = SpeculativeModeClass.Draftless,
            ["ngram-map-k"] = SpeculativeModeClass.Draftless,
            ["ngram-map-k4v"] = SpeculativeModeClass.Draftless,
            ["ngram-mod"] = SpeculativeModeClass.Draftless,
            ["ngram-cache"] = SpeculativeModeClass.Draftless
        };

    /// <summary>Disabled preset — emits no speculative flags. Used as the safe default when no config is supplied.</summary>
    public static SpeculativeDecodingSettings Disabled { get; } =
        new(DisabledMode, DraftModelPath: null, DraftMaxTokens: 0, DraftGpuLayers: null);

    /// <summary>
    ///     Capability class of <paramref name="mode" /> (case-insensitive), or <see langword="null" /> when the mode is
    ///     not recognized; empty/whitespace/<see langword="null" /> collapses to
    ///     <see cref="SpeculativeModeClass.Disabled" />.
    /// </summary>
    /// <remarks>
    ///     The single authority both for which modes exist and for what each one requires, so callers that validate
    ///     operator input — the node-settings boundary, the settings store — never duplicate either.
    /// </remarks>
    public static SpeculativeModeClass? ClassOf(string? mode)
    {
        var trimmed = mode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return SpeculativeModeClass.Disabled;
        }

        foreach (var (allowed, modeClass) in ModeClasses)
        {
            if (string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return modeClass;
            }
        }

        return null;
    }

    /// <summary>
    ///     True when <paramref name="mode" /> is a recognized <c>--spec-type</c> value (case-insensitive), including the
    ///     disabled <c>none</c> mode. Empty/whitespace/<see langword="null" /> collapses to <c>none</c> and is accepted.
    /// </summary>
    public static bool IsAllowedMode(string? mode)
    {
        return ClassOf(mode) is not null;
    }

    /// <summary>
    ///     True only for <see cref="SpeculativeModeClass.ExternalDraft" /> modes — the ones that run a second GGUF and
    ///     so REQUIRE a draft model to launch. Every other mode is false, <c>draft-mtp</c> and an unknown one included.
    /// </summary>
    /// <remarks>
    ///     The static authority for the cross-field "this mode needs a draft model" rule, so the node-settings boundary
    ///     and handler never re-derive it from the mode name.
    /// </remarks>
    public static bool ModeRequiresDraftModel(string? mode)
    {
        return ClassOf(mode) is SpeculativeModeClass.ExternalDraft;
    }

    /// <summary>
    ///     Canonical mode: empty/whitespace collapses to <see cref="DisabledMode" />; a case-insensitive match against the
    ///     accepted set resolves to that set's canonical lowercase form (so operator casing is forgiven); anything else is
    ///     returned trimmed as-is for <see cref="TryValidate" /> to reject.
    /// </summary>
    public string NormalizedMode
    {
        get
        {
            var trimmed = Mode?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return DisabledMode;
            }

            return ModeClasses.Keys.FirstOrDefault(mode => string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase))
                   ?? trimmed;
        }
    }

    /// <summary>Capability class of the configured mode, or <see langword="null" /> when the mode is unrecognized.</summary>
    public SpeculativeModeClass? ModeClass => ClassOf(Mode);

    /// <summary>True when a non-<c>none</c> mode is configured (some speculative flags will be emitted).</summary>
    public bool IsEnabled => !string.Equals(NormalizedMode, DisabledMode, StringComparison.Ordinal);

    /// <summary>
    ///     True only for <see cref="SpeculativeModeClass.ExternalDraft" /> modes, which load a second GGUF and
    ///     therefore need <see cref="DraftModelPath" />.
    /// </summary>
    /// <remarks>
    ///     Every other class is false — including <c>draft-mtp</c>, whose drafter lives in the main model — so no
    ///     missing-draft check ever fires for a mode that has no draft model to miss.
    /// </remarks>
    public bool RequiresExternalDraftModel => ModeClass is SpeculativeModeClass.ExternalDraft;

    /// <summary>
    ///     Validates the config for an emittable combination: a known <c>--spec-type</c>, and a non-empty
    ///     <see cref="DraftModelPath" /> when the mode loads an external draft model.
    /// </summary>
    /// <remarks>
    ///     Pure — the draft file's existence on disk is a separate spawn-path check. The <paramref name="error" />
    ///     returned with <c>false</c> is safe to surface: it carries only the operator-supplied mode string, never an
    ///     internal path.
    /// </remarks>
    public bool TryValidate(out string? error)
    {
        if (!IsEnabled)
        {
            error = null;
            return true;
        }

        if (ModeClass is null)
        {
            error = $"Unknown speculative decoding mode '{Mode}'. Valid modes are: {string.Join(", ", ModeClasses.Keys)}.";
            return false;
        }

        if (RequiresExternalDraftModel && string.IsNullOrWhiteSpace(DraftModelPath))
        {
            error = $"Speculative decoding mode '{NormalizedMode}' needs a draft model, but no draft model path is configured.";
            return false;
        }

        error = null;
        return true;
    }
}
