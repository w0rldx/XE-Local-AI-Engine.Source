namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Immutable, validated view of the chat-role speculative-decoding launch settings the supervisor turns into
///     <c>--spec-*</c> flags. Speculative decoding drafts several tokens cheaply and verifies them in one target pass,
///     raising single-user throughput; it applies to the chat role only (an embedding server does one-shot forward
///     passes with nothing to draft). Two families:
///     <list type="bullet">
///         <item>
///             <c>draft-*</c> (<c>draft-simple</c>/<c>draft-eagle3</c>/<c>draft-mtp</c>) run a second, smaller GGUF as the
///             drafter — they REQUIRE <see cref="DraftModelPath" /> and consume extra VRAM for that model.
///         </item>
///         <item>
///             <c>ngram-*</c> self-speculate from the prompt/context with no draft model file and no extra VRAM.
///         </item>
///     </list>
///     Mode values are validated against the pinned llama-server build (b9692); <see cref="DisabledMode" /> (the default)
///     emits nothing. Like the chat cache-reuse window, these are server-launch flags, orthogonal to the frozen
///     inference profile and NOT part of its identity — changing them never invalidates a stored profile, it only takes
///     effect on the next natural (re)spawn.
/// </summary>
/// <param name="Mode">Raw <c>--spec-type</c> value from config; <c>null</c>/empty/<c>none</c> disables.</param>
/// <param name="DraftModelPath">Path to the draft GGUF; required by <c>draft-*</c> modes, ignored by <c>ngram-*</c>.</param>
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
    ///     Every <c>--spec-type</c> value accepted by the pinned llama-server build (b9692), verified against the
    ///     binary's <c>--help</c>. Kept lowercase; <see cref="NormalizedMode" /> lowercases operator input before lookup.
    /// </summary>
    private static readonly IReadOnlySet<string> AllowedModes = new HashSet<string>(StringComparer.Ordinal)
    {
        DisabledMode,
        "draft-simple",
        "draft-eagle3",
        "draft-mtp",
        "ngram-simple",
        "ngram-map-k",
        "ngram-map-k4v",
        "ngram-mod",
        "ngram-cache"
    };

    /// <summary>Disabled preset — emits no speculative flags. Used as the safe default when no config is supplied.</summary>
    public static SpeculativeDecodingSettings Disabled { get; } =
        new(DisabledMode, DraftModelPath: null, DraftMaxTokens: 0, DraftGpuLayers: null);

    /// <summary>
    ///     True when <paramref name="mode" /> is a recognized <c>--spec-type</c> value (case-insensitive), including the
    ///     disabled <c>none</c> mode. Empty/whitespace/<see langword="null" /> collapses to <c>none</c> and is accepted.
    ///     The single authority for which modes exist, so callers that validate operator input (node-settings boundary,
    ///     the settings store) never duplicate the accepted set.
    /// </summary>
    public static bool IsAllowedMode(string? mode)
    {
        var trimmed = mode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        return AllowedModes.Any(allowed => string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     True when <paramref name="mode" /> is a recognized <c>draft-*</c> mode (case-insensitive) — i.e. a mode that
    ///     REQUIRES a draft model to launch. <c>ngram-*</c>, <c>none</c>, empty, and unknown modes are false. The static
    ///     authority for the cross-field "draft mode needs a draft model" rule so the node-settings boundary + handler
    ///     never re-derive the draft-family test.
    /// </summary>
    public static bool ModeRequiresDraftModel(string? mode)
    {
        var trimmed = mode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var canonical = AllowedModes.FirstOrDefault(allowed => string.Equals(allowed, trimmed, StringComparison.OrdinalIgnoreCase));
        return canonical is not null && canonical.StartsWith("draft-", StringComparison.Ordinal);
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

            return AllowedModes.FirstOrDefault(mode => string.Equals(mode, trimmed, StringComparison.OrdinalIgnoreCase))
                   ?? trimmed;
        }
    }

    /// <summary>True when a non-<c>none</c> mode is configured (some speculative flags will be emitted).</summary>
    public bool IsEnabled => !string.Equals(NormalizedMode, DisabledMode, StringComparison.Ordinal);

    /// <summary>
    ///     True only for the known draft-model modes (<c>draft-*</c>), which require a draft GGUF; <c>ngram-*</c> and
    ///     unknown modes are false, so a missing-draft check never fires for a self-speculating or invalid mode.
    /// </summary>
    public bool IsDraftMode => AllowedModes.Contains(NormalizedMode) && NormalizedMode.StartsWith("draft-", StringComparison.Ordinal);

    /// <summary>
    ///     Validates the config for an emittable combination: a known <c>--spec-type</c>, and a non-empty
    ///     <see cref="DraftModelPath" /> when the mode is <c>draft-*</c>. Pure — the draft file's existence on disk is a
    ///     separate spawn-path check. Returns <c>false</c> with a sanitized, user-safe <paramref name="error" /> (safe to
    ///     surface: it carries only the operator-supplied mode string, never an internal path).
    /// </summary>
    public bool TryValidate(out string? error)
    {
        if (!IsEnabled)
        {
            error = null;
            return true;
        }

        if (!AllowedModes.Contains(NormalizedMode))
        {
            error = $"Unknown speculative decoding mode '{Mode}'. Valid modes are: {string.Join(", ", AllowedModes)}.";
            return false;
        }

        if (IsDraftMode && string.IsNullOrWhiteSpace(DraftModelPath))
        {
            error = $"Speculative decoding mode '{NormalizedMode}' needs a draft model, but no draft model path is configured.";
            return false;
        }

        error = null;
        return true;
    }
}
