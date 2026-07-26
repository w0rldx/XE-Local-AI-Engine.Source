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
/// </remarks>
internal static partial class LlamaFitParamsOutputParser
{
    /// <summary>
    ///     Attempts to build a replay draft from <paramref name="fitParamsOutput" />. Returns <see langword="null" />
    ///     when no complete machine-readable line can be located.
    /// </summary>
    public static ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput)
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
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var gpuLayers)
                || ctxSize <= 0)
            {
                continue;
            }

            var tensorSplit = OptionalValue(match, "ts");
            var overrideTensor = OptionalValue(match, "ot");
            return ResolvedLaunchArguments.Replay(ctxSize, gpuLayers, tensorSplit, overrideTensor);
        }

        return null;
    }

    private static string? OptionalValue(Match match, string groupName)
    {
        var group = match.Groups[groupName];
        return group.Success && !string.IsNullOrWhiteSpace(group.Value) ? group.Value : null;
    }

    [GeneratedRegex(
        """^\s*-c\s+(?<ctx>\d+)\s+-ngl\s+(?<ngl>\d+)(?:\s+-ts\s+(?<ts>\S+))?(?:\s+-ot\s+"(?<ot>[^"]+)")?\s*$""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OutputLineRegex();
}
