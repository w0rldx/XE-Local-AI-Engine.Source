namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>The value rules for <see cref="StoredNodeSettings" /> fields, shared by the store, the policy and the endpoint validators.</summary>
public static partial class StoredNodeSettingsValidation
{
    [GeneratedRegex(StoredNodeSettings.RecommendedLlamaCppTagPattern, RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RecommendedTagRegex();

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="tag" /> matches the pinned-tag format (<c>b</c>+digits).
    /// </summary>
    public static bool IsValidRecommendedLlamaCppTag(string? tag)
    {
        return !string.IsNullOrWhiteSpace(tag) && RecommendedTagRegex().IsMatch(tag);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is a recognized <c>--spec-type</c> value (or
    ///     empty/<c>none</c>, i.e. disabled). Delegates to <see cref="SpeculativeDecodingSettings.IsAllowedMode" /> so the
    ///     accepted set has one authority (the pinned-build-verified list in the provider).
    /// </summary>
    public static bool IsValidSpeculativeMode(string? mode)
    {
        return SpeculativeDecodingSettings.IsAllowedMode(mode);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is an EXTERNAL-DRAFT speculative mode — one
    ///     that runs a second GGUF and so REQUIRES a draft model.
    /// </summary>
    /// <remarks>
    ///     <c>draft-mtp</c> drafts from heads inside the main model and is false here despite the name prefix.
    ///     Delegates to <see cref="SpeculativeDecodingSettings.ModeRequiresDraftModel" /> so the boundary validator and
    ///     the save-endpoint cross-field guard share one authority for the classification.
    /// </remarks>
    public static bool SpeculativeModeRequiresDraftModel(string? mode)
    {
        return SpeculativeDecodingSettings.ModeRequiresDraftModel(mode);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="value" /> names a container runtime this engine knows
    ///     (<c>auto</c> or <c>docker</c>, ordinal-ignore-case). Delegates to
    ///     <see cref="ContainerRuntimeSelectionParser.TryParse" /> so the stored, wire and engine representations of the
    ///     selection have one authority rather than an allow-list restated per caller.
    /// </summary>
    public static bool IsValidContainerRuntimeSelection(string? value)
    {
        return ContainerRuntimeSelectionParser.TryParse(value, out _);
    }

    /// <summary>
    ///     Shape only, never "is it installed": a default is legitimately set before its download. At most
    ///     <see cref="StoredNodeSettings.MaxModelNameLength" /> characters after trimming, and no control characters.
    /// </summary>
    public static bool IsPlausibleModelName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= StoredNodeSettings.MaxModelNameLength && !trimmed.Any(char.IsControl);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="value" /> is one GGUF quant token in its canonical
    ///     spelling, case aside. The quant parser IS the repo's quant vocabulary.
    /// </summary>
    public static bool IsKnownQuant(string value)
    {
        var trimmed = value.Trim();
        return string.Equals(GgufQuantParser.ParseOrNull(trimmed), trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="type" /> is a recognized KV-cache type (or empty, i.e.
    ///     "use the node default"). Delegates to <see cref="LlamaServerKvCacheTypes.IsAllowed" /> so the allow-list has
    ///     one authority shared with the benchmark KV picker.
    /// </summary>
    public static bool IsValidKvCacheType(string? type)
    {
        return LlamaServerKvCacheTypes.IsAllowed(type);
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is one of the four PERSISTABLE
    ///     external-access literals: <see cref="StoredNodeSettings.ExternalAccessProfileRecommended" />,
    ///     <see cref="StoredNodeSettings.ExternalAccessProfileOffline" />, <see cref="StoredNodeSettings.ExternalAccessProfileCustom" />,
    ///     <see cref="StoredNodeSettings.ExternalAccessProfilePending" />.
    /// </summary>
    /// <remarks>
    ///     This is what may be STORED, so <c>NodeSettingsStore.Normalize</c> uses it. The comparison is ordinal (a
    ///     constant string pattern), so <c>"Offline"</c> is rejected rather than silently accepted.
    ///     <see langword="null" /> is a state (undecided), not a literal, and is <see langword="false" /> here.
    /// </remarks>
    public static bool IsValidExternalAccessProfile(string? profile)
    {
        return profile is StoredNodeSettings.ExternalAccessProfileRecommended
            or StoredNodeSettings.ExternalAccessProfileOffline
            or StoredNodeSettings.ExternalAccessProfileCustom
            or StoredNodeSettings.ExternalAccessProfilePending;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is a preset a CLIENT may send:
    ///     <see cref="StoredNodeSettings.ExternalAccessProfileRecommended" /> or <see cref="StoredNodeSettings.ExternalAccessProfileOffline" />.
    ///     <see cref="StoredNodeSettings.ExternalAccessProfileCustom" /> and <see cref="StoredNodeSettings.ExternalAccessProfilePending" /> are engine-written
    ///     states — the boundary validator rejects them as inputs and the save mapper is their only writer.
    /// </summary>
    public static bool IsExternalAccessPreset(string? profile)
    {
        return profile is StoredNodeSettings.ExternalAccessProfileRecommended or StoredNodeSettings.ExternalAccessProfileOffline;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is one of the two navigation modes,
    ///     <see cref="StoredNodeSettings.UiModeSimple" /> or <see cref="StoredNodeSettings.UiModeAdvanced" />.
    /// </summary>
    /// <remarks>
    ///     The comparison is ordinal (a constant string pattern), so <c>"Simple"</c> is rejected rather than silently
    ///     accepted. <see langword="null" /> is a state (not answered yet), not a literal, and is
    ///     <see langword="false" /> here. Unlike the external-access profile there is no engine-written third literal:
    ///     the client may send either value it may store.
    /// </remarks>
    public static bool IsValidUiMode(string? mode)
    {
        return mode is StoredNodeSettings.UiModeSimple or StoredNodeSettings.UiModeAdvanced;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is one of the three PERSISTABLE sandbox security profile literals:
    ///     <see cref="StoredNodeSettings.SandboxSecurityProfileLow" />, <see cref="StoredNodeSettings.SandboxSecurityProfileHigh" />, <see cref="StoredNodeSettings.SandboxSecurityProfilePending" />.
    /// </summary>
    /// <remarks>
    ///     Ordinal, like the external-access literals, so <c>"High"</c> is rejected rather than silently accepted. <see langword="null" /> is
    ///     a state (undecided), not a literal, and is <see langword="false" /> here.
    /// </remarks>
    public static bool IsValidSandboxSecurityProfile(string? profile)
    {
        return profile is StoredNodeSettings.SandboxSecurityProfileLow or StoredNodeSettings.SandboxSecurityProfileHigh or StoredNodeSettings.SandboxSecurityProfilePending;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="profile" /> is a profile a CLIENT may send: <see cref="StoredNodeSettings.SandboxSecurityProfileLow" />
    ///     or <see cref="StoredNodeSettings.SandboxSecurityProfileHigh" />. <see cref="StoredNodeSettings.SandboxSecurityProfilePending" /> is engine-written only.
    /// </summary>
    public static bool IsSandboxSecurityProfileChoice(string? profile)
    {
        return profile is StoredNodeSettings.SandboxSecurityProfileLow or StoredNodeSettings.SandboxSecurityProfileHigh;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="effort" /> is a level with a reasoning budget:
    ///     <c>minimal</c>, <c>low</c>, <c>medium</c> or <c>high</c>.
    /// </summary>
    /// <remarks>Ordinal, like the other literals; <c>none</c> is excluded because it sends no budget at all.</remarks>
    public static bool IsValidDefaultReasoningEffort(string? effort)
    {
        return effort is "minimal" or "low" or "medium" or "high";
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="mode" /> is a chat output-cap variant: <c>cap</c> (cap
    ///     plus notice), <c>notice</c> (notice only, no cap) or <c>off</c>.
    /// </summary>
    public static bool IsValidChatOutputCapMode(string? mode)
    {
        return mode is StoredNodeSettings.ChatOutputCapModeCap or StoredNodeSettings.ChatOutputCapModeNotice or StoredNodeSettings.ChatOutputCapModeOff;
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="channel" /> is one of the three update-channel
    ///     literals: <c>stable</c>, <c>preview</c> or <c>development</c>.
    /// </summary>
    /// <remarks>
    ///     Delegates to <see cref="AppUpdateChannelNames.TryParse" /> so the literals are declared once, not re-listed
    ///     here. The comparison is ordinal, so <c>"Stable"</c> is rejected rather than silently accepted.
    ///     <see langword="null" /> is a state (never chosen), not a literal, and is <see langword="false" /> here.
    /// </remarks>
    public static bool IsValidUpdateChannel(string? channel)
    {
        return AppUpdateChannelNames.TryParse(channel, out _);
    }
}
