namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Compatibility result for a final llama-server launch vector.</summary>
internal sealed record LlamaServerCapabilityDecision(
    LlamaServerLaunchSpec Spec,
    bool IsCompatible,
    bool CanTrySafeFallback,
    string? SanitizedError,
    IReadOnlyList<string> OmittedOptions);

/// <summary>
///     Refuses a runtime missing correctness/safety flags and removes only explicitly optional optimization or
///     diagnostic flags. This keeps bring-your-own/source builds honest without hard-coding behavior from a remembered
///     upstream tag.
/// </summary>
internal static class LlamaServerCapabilityGate
{
    /// <summary>
    ///     The stable token an expert-offload refusal carries, so support and tests can match the cause rather than the
    ///     message. Never localized.
    /// </summary>
    internal const string ExpertOffloadRequiresCpuMoe = "expert-offload-requires-cpu-moe";

    private static readonly IReadOnlyDictionary<string, int> OptionalOptions = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["--cache-reuse"] = 1,
        ["-lv"] = 1,
        ["--metrics"] = 0
    };

    private static readonly IReadOnlySet<string> KvFlashAttentionOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "-ctk",
        "--cache-type-k",
        "-ctv",
        "--cache-type-v",
        "-fa",
        "--flash-attn"
    };

    internal static LlamaServerCapabilityDecision Apply(LlamaServerLaunchSpec spec,
        LlamaServerCapabilityManifest manifest,
        bool requireMetrics)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.ProbeSucceeded)
        {
            return Incompatible(spec, "The selected llama.cpp runtime could not report its supported server options. Reinstall or rebuild the runtime and try again.");
        }

        var arguments = spec.Arguments.ToList();
        var omitted = new List<string>();
        var emittedKvFlashOptions = KvFlashAttentionOptions.Where(arguments.Contains).ToArray();
        if (emittedKvFlashOptions.Length > 0
            && emittedKvFlashOptions.Any(option => !SupportsLaunchOption(manifest, option)))
        {
            return Incompatible(spec,
                "The selected llama.cpp runtime does not support the profile's KV-cache and Flash Attention options. Recalibrate this profile for the selected runtime.",
                canTrySafeFallback: true);
        }

        // --cpu-moe is deliberately NOT in OptionalOptions. The flag is the placement: ProcessContextAllocationResolver
        // reserved (GpuBytes, max(CpuBytes, fileSize)) on the premise that the whole expert share sits in system RAM,
        // so a runtime that cannot take the flag cannot honour that admission. Refuse rather than degrade — and never
        // with canTrySafeFallback, which would drop the flag and launch the very over-subscription this prevents.
        if (arguments.Contains("--cpu-moe") && !SupportsLaunchOption(manifest, "--cpu-moe"))
        {
            return Incompatible(spec,
                "The selected llama.cpp runtime does not support '--cpu-moe', which this model needs to keep its experts "
                + "in system RAM. Install a runtime that supports it, or use a model that fits in VRAM. "
                + "(" + ExpertOffloadRequiresCpuMoe + ")");
        }

        if (!SupportsKvAndFlashValues(arguments, manifest))
        {
            return Incompatible(spec,
                "The selected llama.cpp runtime does not support the profile's KV-cache type or Flash Attention mode. Recalibrate this profile for the selected runtime.",
                canTrySafeFallback: true);
        }

        foreach (var (option, valueCount) in OptionalOptions)
        {
            if (string.Equals(option, "--metrics", StringComparison.Ordinal) && requireMetrics)
            {
                continue;
            }

            if (!SupportsLaunchOption(manifest, option) && RemoveOption(arguments, option, valueCount))
            {
                omitted.Add(option);
            }
        }

        foreach (var argument in arguments)
        {
            if (!TryGetOptionName(argument, out var option) || SupportsLaunchOption(manifest, option))
            {
                continue;
            }

            return Incompatible(spec,
                $"The selected llama.cpp runtime does not support required server option '{option}'. Install a compatible runtime and try again.",
                omitted: omitted);
        }

        var specTypeIndex = arguments.IndexOf("--spec-type");
        if (specTypeIndex >= 0
            && specTypeIndex + 1 < arguments.Count
            && !manifest.SupportsSpeculativeMode(arguments[specTypeIndex + 1]))
        {
            return Incompatible(spec,
                $"The selected llama.cpp runtime does not support speculative mode '{arguments[specTypeIndex + 1]}'. Choose a supported mode or update the runtime.",
                omitted: omitted);
        }

        var adjusted = spec with
        {
            Arguments = arguments
        };
        return new LlamaServerCapabilityDecision(adjusted,
            IsCompatible: true,
            CanTrySafeFallback: false,
            SanitizedError: null,
            omitted);
    }

    private static bool SupportsLaunchOption(LlamaServerCapabilityManifest manifest, string option)
    {
        return manifest.SupportsOption(option);
    }

    private static bool SupportsKvAndFlashValues(IReadOnlyList<string> arguments, LlamaServerCapabilityManifest manifest)
    {
        return SupportsValue(arguments, "-ctk", manifest.SupportsCacheTypeK)
               && SupportsValue(arguments, "-ctv", manifest.SupportsCacheTypeV)
               && SupportsValue(arguments, "-fa", manifest.SupportsFlashAttentionMode)
               && SupportsValue(arguments, "--flash-attn", manifest.SupportsFlashAttentionMode);
    }

    private static bool SupportsValue(IReadOnlyList<string> arguments, string option, Func<string, bool> supportsValue)
    {
        var index = -1;
        for (var candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (string.Equals(arguments[candidate], option, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        return index < 0 || (index + 1 < arguments.Count && supportsValue(arguments[index + 1]));
    }

    private static bool TryGetOptionName(string argument, out string option)
    {
        option = string.Empty;
        if (string.IsNullOrEmpty(argument)
            || argument[0] != '-'
            || (argument.Length > 1 && char.IsDigit(argument[1])))
        {
            return false;
        }

        var equalsIndex = argument.IndexOf('=', StringComparison.Ordinal);
        option = equalsIndex < 0 ? argument : argument[..equalsIndex];
        return option.Length > 1;
    }

    private static bool RemoveOption(List<string> arguments, string option, int valueCount)
    {
        var removed = false;
        for (var index = arguments.Count - 1; index >= 0; index--)
        {
            if (!string.Equals(arguments[index], option, StringComparison.Ordinal))
            {
                continue;
            }

            var removeCount = Math.Min(valueCount + 1, arguments.Count - index);
            arguments.RemoveRange(index, removeCount);
            removed = true;
        }

        return removed;
    }

    private static LlamaServerCapabilityDecision Incompatible(LlamaServerLaunchSpec spec,
        string sanitizedError,
        bool canTrySafeFallback = false,
        IReadOnlyList<string>? omitted = null)
    {
        return new LlamaServerCapabilityDecision(spec, IsCompatible: false, canTrySafeFallback, sanitizedError, omitted ?? []);
    }
}
