namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Parses the single machine-readable stdout line emitted by <c>llama-fit-params</c> into a frozen
///     <see cref="ResolvedLaunchArguments.Replay" /> draft.
/// </summary>
/// <remarks>
///     The grammar is emitted by llama.cpp's dedicated tool, not scraped from variable startup logs:
///     <c>-c N -ngl N [-ts N0,N1,...] [-ot "pattern=buffer,..."]</c>. Both <c>-c</c> and <c>-ngl</c> are required.
///     A missing placement field therefore fails the parse instead of silently producing a context-only profile.
///     The helper can return unchanged defaults when the initial parameters already fit (observed on b9692; the
///     parser tolerates it on any pin): <c>-c 0</c> means the
///     model-trained context and <c>-ngl -1</c> means automatic placement. Context zero is never concrete. Automatic
///     placement is normalized to explicit all-layers (<c>-2</c>) only when verbose startup output proves full offload.
///     Expert placement is preserved the same way KV/flash-attention policy is — from the successful argv: a spawn that
///     carried <c>--cpu-moe</c> must yield an <c>-ot</c>, because the helper turns that flag into the equivalent tensor
///     override and echoes it. A fit line without one is not a replayable placement and fails the parse.
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

            // The spawn kept the experts in system RAM, so the replay MUST carry that placement or it would launch
            // outside the footprint admission booked for it. The helper echoes --cpu-moe back as -ot, so a missing
            // -ot here means the helper never saw the flag (or dropped it): no concrete replay can be proven.
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
