namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Parses the single machine-readable stdout line emitted by <c>llama-fit-params</c> into a frozen
///     <see cref="ResolvedLaunchArguments.Replay" /> draft.
/// </summary>
/// <remarks>
///     The grammar comes from llama.cpp's dedicated tool, not from scraped startup logs —
///     <c>-c N -ngl N [-ts N0,N1,...] [-ot "pattern=buffer,..."]</c> — with <c>-c</c> and <c>-ngl</c> both required, so
///     a missing placement field fails the parse rather than silently produce a context-only profile. When the initial
///     parameters already fit, the helper can return unchanged defaults instead (observed on b9692, tolerated on any
///     pin), and neither of those defaults is a concrete replay.
/// </remarks>
internal static partial class LlamaFitParamsOutputParser
{
    /// <summary>
    ///     Attempts to build a replay draft from <paramref name="fitParamsOutput" />. Returns <see langword="null" />
    ///     when no complete machine-readable line can be located.
    /// </summary>
    public static ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput,
        IReadOnlyList<string>? startupOutput = null,
        IReadOnlyList<string>? successfulLaunchArguments = null)
    {
        ArgumentNullException.ThrowIfNull(fitParamsOutput);

        if (!TryParseSuccessfulLaunchPolicy(successfulLaunchArguments,
                out var kvTypeK,
                out var kvTypeV,
                out var flashAttn,
                out var expertOffload))
        {
            return null;
        }

        foreach (var line in fitParamsOutput)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var match = OutputLineRegex().Match(line);
            if (!match.Success
                || !int.TryParse(match.Groups["ctx"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ctxSize)
                || !int.TryParse(match.Groups["ngl"].Value,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var gpuLayers)
                || ctxSize <= 0)
            {
                continue;
            }

            // -ngl -1 is the helper's "automatic placement", which is not a concrete replay: it is normalized to explicit all-layers (-2) only
            // when verbose startup output proves full offload, and otherwise rejected. Likewise -c 0 (model-trained context) never passes the guard above.
            if (gpuLayers == -1)
            {
                if (!HasFullGpuOffloadEvidence(startupOutput))
                {
                    continue;
                }

                gpuLayers = -2;
            }

            var tensorSplit = OptionalValue(match, "ts");
            var overrideTensor = OptionalValue(match, "ot");

            // The spawn kept the experts in system RAM, so the replay MUST carry that placement or launch outside the footprint admission booked for it. The helper
            // echoes --cpu-moe back as -ot, so a missing -ot here means it never saw the flag, or dropped it: no concrete replay can be proven.
            if (expertOffload && overrideTensor is null)
            {
                continue;
            }

            return ResolvedLaunchArguments.Replay(ctxSize,
                gpuLayers,
                tensorSplit,
                overrideTensor,
                kvTypeK,
                kvTypeV,
                flashAttn);
        }

        return null;
    }

    private static bool HasFullGpuOffloadEvidence(IReadOnlyList<string>? startupOutput)
    {
        if (startupOutput is null)
        {
            return false;
        }

        var foundPlacement = false;
        foreach (var line in startupOutput)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (LlamaLayerOffloadBanner.TryParse(line, out var offloaded, out var total))
            {
                foundPlacement = true;
                if (offloaded != total)
                {
                    return false;
                }
            }
        }

        return foundPlacement;
    }

    private static bool TryParseSuccessfulLaunchPolicy(IReadOnlyList<string>? arguments,
        out string? kvTypeK,
        out string? kvTypeV,
        out bool flashAttn,
        out bool expertOffload)
    {
        kvTypeK = null;
        kvTypeV = null;
        flashAttn = false;
        expertOffload = false;
        string? flashAttnValue = null;

        if (arguments is null)
        {
            return true;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "-ctk" or "--cache-type-k")
            {
                if (!TryReadSingleValue(arguments, ref index, ref kvTypeK))
                {
                    return false;
                }
            }
            else if (argument is "-ctv" or "--cache-type-v")
            {
                if (!TryReadSingleValue(arguments, ref index, ref kvTypeV))
                {
                    return false;
                }
            }
            else if (argument is "-cmoe" or "--cpu-moe" or "-ncmoe" or "--n-cpu-moe")
            {
                expertOffload = true;
            }
            else if ((argument is "-fa" or "--flash-attn")
                     && !TryReadSingleValue(arguments, ref index, ref flashAttnValue))
            {
                return false;
            }
        }

        if (flashAttnValue is not null)
        {
            if (!string.Equals(flashAttnValue, "on", StringComparison.Ordinal))
            {
                return false;
            }

            flashAttn = true;
        }

        var kvKeySet = kvTypeK is not null;
        var kvValueSet = kvTypeV is not null;
        return kvKeySet
            ? kvValueSet
              && flashAttn
              && string.Equals(kvTypeK, kvTypeV, StringComparison.Ordinal)
            : !kvValueSet && !flashAttn;
    }

    private static bool TryReadSingleValue(IReadOnlyList<string> arguments,
        ref int index,
        ref string? value)
    {
        if (value is not null || index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            return false;
        }

        value = arguments[++index];
        return true;
    }

    private static string? OptionalValue(Match match, string groupName)
    {
        var group = match.Groups[groupName];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value) ? group.Value : null;
    }

    [GeneratedRegex("""^\s*-c\s+(?<ctx>\d+)\s+-ngl\s+(?<ngl>-?\d+)(?:\s+-ts\s+(?<ts>\S+))?(?:\s+-ot\s+"(?<ot>[^"]+)")?\s*$""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OutputLineRegex();
}
