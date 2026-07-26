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
///     A missing placement field therefore fails the parse instead of silently producing a context-only profile. At
///     b9692 the helper can return unchanged defaults when the initial parameters already fit: <c>-c 0</c> means the
///     model-trained context and <c>-ngl -1</c> means automatic placement. Context zero is never concrete. Automatic
///     placement is normalized to explicit all-layers (<c>-2</c>) only when verbose startup output proves full offload.
/// </remarks>
internal static partial class LlamaFitParamsOutputParser
{
    /// <summary>
    ///     Attempts to build a replay draft from <paramref name="fitParamsOutput" />. Returns <see langword="null" />
    ///     when no complete machine-readable line can be located.
    /// </summary>
    public static ResolvedLaunchArguments? TryParseFittedArgs(
        IReadOnlyList<string> fitParamsOutput,
        IReadOnlyList<string>? startupOutput = null)
    {
        ArgumentNullException.ThrowIfNull(fitParamsOutput);

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
            return ResolvedLaunchArguments.Replay(ctxSize, gpuLayers, tensorSplit, overrideTensor);
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

            var match = FullGpuOffloadRegex().Match(line);
            if (match.Success
                && int.TryParse(match.Groups["offloaded"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var offloaded)
                && int.TryParse(match.Groups["total"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var total)
                && total > 0)
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

    private static string? OptionalValue(Match match, string groupName)
    {
        var group = match.Groups[groupName];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value) ? group.Value : null;
    }

    [GeneratedRegex(
        """^\s*-c\s+(?<ctx>\d+)\s+-ngl\s+(?<ngl>-?\d+)(?:\s+-ts\s+(?<ts>\S+))?(?:\s+-ot\s+"(?<ot>[^"]+)")?\s*$""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OutputLineRegex();

    [GeneratedRegex(
        """\boffloaded\s+(?<offloaded>\d+)\s*/\s*(?<total>\d+)\s+layers?\s+to\s+GPU\b""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullGpuOffloadRegex();
}
