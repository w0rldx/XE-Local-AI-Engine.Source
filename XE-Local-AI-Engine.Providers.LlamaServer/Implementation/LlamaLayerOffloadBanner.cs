namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
///     The single grammar for llama.cpp's model-load layer-placement banner
///     (<c>load_tensors: offloaded 25/25 layers to GPU</c>) — the ONLY place llama.cpp reports how many of a model's
///     layers actually landed on the GPU. <c>/props</c> exposes no device, backend, or <c>n_gpu_layers</c> field, so
///     stdout is the sole source.
/// </summary>
/// <remarks>
///     The banner is emitted only above the default log verbosity: at llama-server's default (<c>-lv 3</c>) the whole
///     startup is 11 lines and carries no placement line at all. Callers that need the banner must raise verbosity on
///     the spawn they want to observe.
/// </remarks>
internal static partial class LlamaLayerOffloadBanner
{
    /// <summary>
    ///     Attempts to read the offloaded / total layer counts out of one stdout line. Returns <see langword="false" />
    ///     for any line that is not the banner, or whose total is not a positive count.
    /// </summary>
    public static bool TryParse(string? line, out int offloaded, out int total)
    {
        offloaded = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = OffloadBannerRegex().Match(line);
        return match.Success
               && int.TryParse(match.Groups["offloaded"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out offloaded)
               && int.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out total)
               && total > 0;
    }

    [GeneratedRegex(
        """\boffloaded\s+(?<offloaded>\d+)\s*/\s*(?<total>\d+)\s+layers?\s+to\s+GPU\b""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OffloadBannerRegex();
}
