namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     What a <c>--spec-type</c> mode needs to launch, and which draft flags it may emit. The <c>draft-</c> name prefix
///     spans two classes and is NOT a capability test — every allowed mode is mapped to a class explicitly in
///     <see cref="SpeculativeDecodingSettings" />, so adding a mode forces that choice rather than inheriting behaviour
///     from its name.
/// </summary>
public enum SpeculativeModeClass
{
    /// <summary>Speculation off (<c>none</c>) — no <c>--spec-*</c> flag is emitted at all.</summary>
    Disabled,

    /// <summary>
    ///     Runs a SECOND GGUF as the drafter (<c>draft-simple</c>, <c>draft-eagle3</c>, <c>draft-dflash</c>,
    ///     <c>draft-dspark</c>): requires a draft model path and emits <c>--spec-draft-model</c>, plus the draft-model
    ///     offload knob <c>--spec-draft-ngl</c>. Costs that model's weights + KV on top of the target.
    /// </summary>
    ExternalDraft,

    /// <summary>
    ///     Drafts from multi-token-prediction heads inside the MAIN model GGUF (<c>draft-mtp</c>): no second model exists,
    ///     so no draft-model flag may be emitted. b10201 builds the MTP draft context over the target model
    ///     (<c>server-context.cpp</c>: "MTP draft context lives on the target model, only context+compute are new"), so it
    ///     still costs extra context/compute VRAM — just not a second set of weights.
    /// </summary>
    MainModelHeads,

    /// <summary>
    ///     Self-speculates from the prompt/context (<c>ngram-*</c>): no draft weights, no extra VRAM, and only
    ///     <c>--spec-type</c> is emitted (the drafting knobs are the mode-specific <c>--spec-ngram-*</c> flags).
    /// </summary>
    Draftless
}

/// <summary>
///     Immutable, validated view of the chat-role speculative-decoding launch settings the supervisor turns into
///     <c>--spec-*</c> flags. Speculative decoding drafts several tokens cheaply and verifies them in one target pass,
///     raising single-user throughput; it applies to the chat role only (an embedding server does one-shot forward
///     passes with nothing to draft). Three capability classes — see <see cref="SpeculativeModeClass" /> — of which only
///     <see cref="SpeculativeModeClass.ExternalDraft" /> involves a second GGUF: <c>draft-mtp</c> drafts from heads in
///     the main model and needs NO <see cref="DraftModelPath" />, and a path configured alongside it is ignored rather
///     than rejected (settings persisted before this contract was corrected still carry one, and rejecting would turn
///     them into a non-retryable launch failure on upgrade).
///     Mode values are validated against the pinned llama-server build (b10201); <see cref="DisabledMode" /> (the default)
///     emits nothing. Like the chat cache-reuse window, these are server-launch flags, orthogonal to the frozen
///     inference profile and NOT part of its identity — changing them never invalidates a stored profile, it only takes
///     effect on the next natural (re)spawn.
/// </summary>
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
    ///     The <c>--spec-type</c> values this application exposes, each mapped to its capability class and each verified
    ///     accepted by the pinned llama-server build (b10201 <c>--help</c>, re-probed 2026-08-07).
    ///     <c>draft-dflash</c> and <c>draft-dspark</c> are offered too, both as
    ///     <see cref="SpeculativeModeClass.ExternalDraft" />: each loads a second GGUF passed with
    ///     <c>--spec-draft-model</c>, so the existing external-draft plumbing covers them and no mode-specific field is
    ///     needed. Upstream clamps <c>--spec-draft-n-max</c> DOWN to the draft model's trained block size (its examples
    ///     use 15 for DFlash and 7 for DSpark), so the operator must raise the value from its default of 3 — the clamp
    ///     never raises it. With those two added the exposed set equals b10201's accepted set exactly. Kept lowercase;
    ///     <see cref="NormalizedMode" /> matches operator input case-insensitively and resolves to these keys.
    /// </summary>
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
    ///     Capability class of <paramref name="mode" /> (case-insensitive), or <see langword="null" /> when the mode is not
    ///     recognized. Empty/whitespace/<see langword="null" /> collapses to <c>none</c> →
    ///     <see cref="SpeculativeModeClass.Disabled" />. The single authority both for which modes exist and for what each
    ///     one requires, so callers that validate operator input (node-settings boundary, the settings store) never
    ///     duplicate either.
    /// </summary>
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
    ///     True only for <see cref="SpeculativeModeClass.ExternalDraft" /> modes — the ones that run a second GGUF and so
    ///     REQUIRE a draft model to launch. <c>draft-mtp</c> (main-model heads), <c>ngram-*</c>, <c>none</c>, empty, and
    ///     unknown modes are false. The static authority for the cross-field "this mode needs a draft model" rule so the
    ///     node-settings boundary + handler never re-derive it from the mode name.
    /// </summary>
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
    ///     True only for <see cref="SpeculativeModeClass.ExternalDraft" /> modes, which load a second GGUF and therefore
    ///     need <see cref="DraftModelPath" />. Every other class — including <c>draft-mtp</c>, whose drafter lives in the
    ///     main model — is false, so no missing-draft check ever fires for a mode that has no draft model to miss.
    /// </summary>
    public bool RequiresExternalDraftModel => ModeClass is SpeculativeModeClass.ExternalDraft;

    /// <summary>
    ///     Validates the config for an emittable combination: a known <c>--spec-type</c>, and a non-empty
    ///     <see cref="DraftModelPath" /> when the mode loads an external draft model. Pure — the draft file's existence on
    ///     disk is a separate spawn-path check. Returns <c>false</c> with a sanitized, user-safe <paramref name="error" />
    ///     (safe to surface: it carries only the operator-supplied mode string, never an internal path).
    /// </summary>
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
