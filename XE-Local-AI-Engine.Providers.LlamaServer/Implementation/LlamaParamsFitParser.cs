namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Parses the fitted launch parameters that llama.cpp's <c>llama_params_fit</c> / <c>--fit on</c> summary prints to
///     the startup streams (captured during an explore-mode profiling spawn) into a frozen
///     <see cref="ResolvedLaunchArguments.Replay" /> draft the operator can later freeze as a profile.
/// </summary>
/// <remarks>
///     <para>
///         <strong>The exact banner format is ASSUMED.</strong> The fit summary's wording varies across llama.cpp
///         builds and GPU backends, and it cannot be reproduced in a no-GPU build/test environment — so each field is
///         parsed by its own deliberately tolerant regex (named groups, case-insensitive, with a match timeout) and a
///         missing field is simply left unset rather than failing the whole parse. The context size is the one required
///         anchor: when it cannot be found this returns <see langword="null" /> so the caller keeps the live
///         <c>--fit</c> result instead of freezing a bad draft. re-verify this parser against REAL GPU
///         <c>llama_params_fit</c> output before relying on a frozen profile — follow-up: capture a genuine fit banner
///         on Windows-NVIDIA and Vulkan and lock the field formats with a fixture.
///     </para>
/// </remarks>
internal static partial class LlamaParamsFitParser
{
    /// <summary>
    ///     Attempts to build a replay draft from the captured <paramref name="startupOutput" />. Returns
    ///     <see langword="null" /> when no fitted context size can be located (the required anchor).
    /// </summary>
    /// <param name="startupOutput">The captured stdout + stderr lines from an explore-mode profiling spawn.</param>
    public static ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> startupOutput)
    {
        ArgumentNullException.ThrowIfNull(startupOutput);

        int? ctxSize = null;
        int? gpuLayers = null;
        string? tensorSplit = null;
        string? overrideTensor = null;

        foreach (var line in startupOutput)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ctxSize ??= MatchInt(ContextSizeRegex(), line);
            gpuLayers ??= MatchInt(GpuLayersRegex(), line);
            tensorSplit ??= MatchString(TensorSplitRegex(), line);
            overrideTensor ??= MatchString(OverrideTensorRegex(), line);
        }

        // The context size is the required anchor — without it the draft would be unsafe to freeze.
        if (ctxSize is not { } ctx)
        {
            return null;
        }

        return ResolvedLaunchArguments.Replay(ctx, gpuLayers, tensorSplit, overrideTensor);
    }

    private static int? MatchInt(Regex regex, string line)
    {
        var match = regex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var raw = match.Groups["value"].Value;
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? MatchString(Regex regex, string line)
    {
        var match = regex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    // Fitted context size: "n_ctx = 8192", "context size: 8192", "context = 8192".
    [GeneratedRegex(@"\b(?:n_ctx|context[ _]?size|context)\s*[=:]\s*(?<value>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContextSizeRegex();

    // Fitted GPU layer count: "n_gpu_layers = 32", "gpu layers: 32", "ngl = 32".
    [GeneratedRegex(@"\b(?:n_gpu_layers|gpu[ _]?layers|ngl)\s*[=:]\s*(?<value>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GpuLayersRegex();

    // Fitted tensor split: "tensor split = 0.6,0.4", "tensor_split: 0.6,0.4", "ts = 0.6,0.4".
    [GeneratedRegex(@"\b(?:tensor[ _]?split|ts)\s*[=:]\s*(?<value>[0-9][0-9.,]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TensorSplitRegex();

    // Fitted override-tensor / expert placement: "override tensor = exps=CPU", "override_tensor: exps=CPU", "ot = exps=CPU".
    [GeneratedRegex(@"\b(?:override[ _]?tensor|ot)\s*[=:]\s*(?<value>\S+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OverrideTensorRegex();
}
