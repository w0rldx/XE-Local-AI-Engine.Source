namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Frozen;
using System.Text.RegularExpressions;

/// <summary>Parsed command-line surface reported by one resolved llama-server executable.</summary>
internal sealed partial record LlamaServerCapabilityManifest
{
    private LlamaServerCapabilityManifest(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc,
        string? executableSha256,
        string? version,
        bool probeSucceeded,
        IReadOnlySet<string> options,
        IReadOnlySet<string> speculativeModes,
        IReadOnlySet<string> cacheTypesK,
        IReadOnlySet<string> cacheTypesV,
        IReadOnlySet<string> flashAttentionModes,
        bool supportsAllOptions)
    {
        Binary = binary;
        ExecutableLengthBytes = executableLengthBytes;
        ExecutableLastWriteUtc = executableLastWriteUtc;
        ExecutableSha256 = executableSha256;
        Version = version;
        ProbeSucceeded = probeSucceeded;
        Options = options;
        SpeculativeModes = speculativeModes;
        CacheTypesK = cacheTypesK;
        CacheTypesV = cacheTypesV;
        FlashAttentionModes = flashAttentionModes;
        SupportsAllOptions = supportsAllOptions;
    }

    public LlamaBinary Binary { get; }

    public long ExecutableLengthBytes { get; }

    public DateTimeOffset ExecutableLastWriteUtc { get; }

    public string? ExecutableSha256 { get; }

    public string? Version { get; }

    public bool ProbeSucceeded { get; }

    public IReadOnlySet<string> Options { get; }

    public IReadOnlySet<string> SpeculativeModes { get; }

    public IReadOnlySet<string> CacheTypesK { get; }

    public IReadOnlySet<string> CacheTypesV { get; }

    public IReadOnlySet<string> FlashAttentionModes { get; }

    /// <summary>
    ///     Set only by <see cref="AllSupportedForTesting" />, which stands in for a binary whose whole option surface is
    ///     assumed. Internal rather than private so <see cref="LlamaServerLaunchCapabilityInspector" /> carries the same
    ///     assumption into its public answers instead of reporting "supports nothing".
    /// </summary>
    internal bool SupportsAllOptions { get; }

    public bool SupportsOption(string option)
    {
        return SupportsAllOptions || Options.Contains(option);
    }

    public bool SupportsSpeculativeMode(string mode)
    {
        return SupportsAllOptions || SpeculativeModes.Contains(mode);
    }

    public bool SupportsCacheTypeK(string cacheType)
    {
        return SupportsAllOptions || CacheTypesK.Contains(cacheType);
    }

    public bool SupportsCacheTypeV(string cacheType)
    {
        return SupportsAllOptions || CacheTypesV.Contains(cacheType);
    }

    public bool SupportsFlashAttentionMode(string mode)
    {
        return SupportsAllOptions || FlashAttentionModes.Contains(mode);
    }

    internal static LlamaServerCapabilityManifest FromSuccessfulProbe(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc,
        string executableSha256,
        string version,
        string help)
    {
        var parsed = ParseHelp(help);
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes,
            executableLastWriteUtc,
            executableSha256,
            version,
            probeSucceeded: true,
            parsed.Options,
            parsed.SpeculativeModes,
            parsed.CacheTypesK,
            parsed.CacheTypesV,
            parsed.FlashAttentionModes,
            supportsAllOptions: false);
    }

    internal static LlamaServerCapabilityManifest Failed(LlamaBinary binary,
        long executableLengthBytes,
        DateTimeOffset executableLastWriteUtc)
    {
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes,
            executableLastWriteUtc,
            executableSha256: null,
            version: null,
            probeSucceeded: false,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            supportsAllOptions: false);
    }

    /// <summary>Permissive test-only manifest used by supervisor fakes that never execute a real llama-server.</summary>
    internal static LlamaServerCapabilityManifest AllSupportedForTesting(LlamaBinary binary)
    {
        return new LlamaServerCapabilityManifest(binary,
            executableLengthBytes: 0,
            DateTimeOffset.UnixEpoch,
            executableSha256: null,
            version: "test",
            probeSucceeded: true,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            FrozenSet<string>.Empty,
            supportsAllOptions: true);
    }

    internal static ParsedLlamaServerHelp ParseHelp(string help)
    {
        if (string.IsNullOrWhiteSpace(help))
        {
            return new ParsedLlamaServerHelp
            {
                Options = FrozenSet<string>.Empty,
                SpeculativeModes = FrozenSet<string>.Empty,
                CacheTypesK = FrozenSet<string>.Empty,
                CacheTypesV = FrozenSet<string>.Empty,
                FlashAttentionModes = FrozenSet<string>.Empty
            };
        }

        var options = help.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                          .Where(static line => line.Length > 0 && line[0] == '-')
                          .Select(static line => OptionDeclarationRegex().Match(line))
                          .Where(static match => match.Success)
                          .SelectMany(static match => match.Groups["option"].Captures.Select(static capture => capture.Value))
                          .ToFrozenSet(StringComparer.Ordinal);
        return new ParsedLlamaServerHelp
        {
            Options = options,
            SpeculativeModes = ParseCommaSeparatedValues(SpeculativeModesRegex().Match(help)),
            CacheTypesK = ParseCommaSeparatedValues(CacheTypesKRegex().Match(help)),
            CacheTypesV = ParseCommaSeparatedValues(CacheTypesVRegex().Match(help)),
            FlashAttentionModes = ParsePipeSeparatedValues(FlashAttentionModesRegex().Match(help))
        };
    }

    private static FrozenSet<string> ParseCommaSeparatedValues(Match match)
    {
        return match.Success
            ? match.Groups["values"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    private static FrozenSet<string> ParsePipeSeparatedValues(Match match)
    {
        return match.Success
            ? match.Groups["values"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToFrozenSet(StringComparer.Ordinal)
            : FrozenSet<string>.Empty;
    }

    [GeneratedRegex(@"^(?<option>--?[A-Za-z][A-Za-z0-9_-]*)(?:\s*,\s*(?<option>--?[A-Za-z][A-Za-z0-9_-]*))*",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OptionDeclarationRegex();

    [GeneratedRegex(@"(?m)^\s*--spec-type\s+(?<values>[A-Za-z0-9_-]+(?:\s*,\s*[A-Za-z0-9_-]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpeculativeModesRegex();

    [GeneratedRegex(@"(?m)^\s*-ctk\s*,\s*--cache-type-k\s+TYPE[^\r\n]*\r?\n\s*allowed values:\s*(?<values>[A-Za-z0-9_]+(?:\s*,\s*[A-Za-z0-9_]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CacheTypesKRegex();

    [GeneratedRegex(@"(?m)^\s*-ctv\s*,\s*--cache-type-v\s+TYPE[^\r\n]*\r?\n\s*allowed values:\s*(?<values>[A-Za-z0-9_]+(?:\s*,\s*[A-Za-z0-9_]+)*)",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CacheTypesVRegex();

    [GeneratedRegex(@"(?m)^\s*-fa\s*,\s*--flash-attn\s+\[(?<values>[A-Za-z0-9_-]+(?:\|[A-Za-z0-9_-]+)*)\]",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex FlashAttentionModesRegex();
}
