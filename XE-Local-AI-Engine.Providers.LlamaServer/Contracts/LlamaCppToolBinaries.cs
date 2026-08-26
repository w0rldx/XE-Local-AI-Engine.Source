namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The llama.cpp helper executables that ship alongside <c>llama-server</c> in a runtime's <c>bin</c> directory.
///     They are located by name next to the resolved server rather than recorded anywhere, exactly as
///     <c>llama-fit-params</c> already is.
/// </summary>
/// <remarks>
///     Presence is never a precondition for serving. <c>llama-quantize</c> is off the inference path entirely — it is
///     used only when a training export converts an f16 GGUF down to a quantized one — and <c>llama-perplexity</c>
///     likewise only runs for a benchmark fidelity measurement — so a runtime without either still serves models
///     correctly and must not be rejected at adoption. Callers that need one check for it and surface a specific
///     "this runtime cannot do X" failure at the point of use.
/// </remarks>
public static class LlamaCppToolBinaries
{
    /// <summary>Base name of the quantizer; <c>.exe</c> is appended on Windows.</summary>
    public const string QuantizerName = "llama-quantize";

    /// <summary>The platform file name of the quantizer.</summary>
    public static string QuantizerFileName => OperatingSystem.IsWindows() ? QuantizerName + ".exe" : QuantizerName;

    /// <summary>
    ///     Returns the absolute path to the quantizer inside <paramref name="binDirectory" />, or
    ///     <see langword="null" /> when this runtime did not ship one (every prebuilt release archive today, and any
    ///     source build produced before the quantizer joined the build targets).
    /// </summary>
    public static string? TryResolveQuantizer(string? binDirectory)
    {
        if (string.IsNullOrWhiteSpace(binDirectory))
        {
            return null;
        }

        var path = Path.Combine(binDirectory, QuantizerFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Resolves the quantizer that sits beside an already-resolved <c>llama-server</c> executable. This is the shape
    ///     the export service uses: it holds a server path, not a bin directory.
    /// </summary>
    public static string? TryResolveQuantizerBesideServer(string? serverExecutablePath) =>
        string.IsNullOrWhiteSpace(serverExecutablePath)
            ? null
            : TryResolveQuantizer(Path.GetDirectoryName(Path.GetFullPath(serverExecutablePath)));

    /// <summary>Base name of the perplexity tool; <c>.exe</c> is appended on Windows.</summary>
    public const string PerplexityName = "llama-perplexity";

    /// <summary>The platform file name of the perplexity tool.</summary>
    public static string PerplexityFileName => OperatingSystem.IsWindows() ? PerplexityName + ".exe" : PerplexityName;

    /// <summary>
    ///     Returns the absolute path to the perplexity tool inside <paramref name="binDirectory" />, or
    ///     <see langword="null" /> when this runtime did not ship one (any source build produced before the tool joined
    ///     the build targets; the prebuilt release archives all carry it).
    /// </summary>
    public static string? TryResolvePerplexity(string? binDirectory)
    {
        if (string.IsNullOrWhiteSpace(binDirectory))
        {
            return null;
        }

        var path = Path.Combine(binDirectory, PerplexityFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    ///     Resolves the perplexity tool that sits beside an already-resolved <c>llama-server</c> executable. This is the
    ///     shape the benchmark fidelity path uses: it holds a server path, not a bin directory.
    /// </summary>
    public static string? TryResolvePerplexityBesideServer(string? serverExecutablePath) =>
        string.IsNullOrWhiteSpace(serverExecutablePath)
            ? null
            : TryResolvePerplexity(Path.GetDirectoryName(Path.GetFullPath(serverExecutablePath)));
}
