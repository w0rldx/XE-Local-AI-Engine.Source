namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Builds the exact, ordered <c>whisper-server</c> startup argument vector for one model on a loopback port, and is
///     the ONLY place whisper-server flag names live.
/// </summary>
/// <remarks>
///     That is what makes the invariant "no whisper-server flag escapes this project" checkable rather than aspirational. Per-request
///     decoding knobs — language, translate, temperature, prompt, VAD on/off, response format — are NOT here: they ride the per-request
///     multipart body, and the server takes a fresh copy of its defaults per request, so nothing set at launch leaks between callers.
///     Startup arguments carry only the resident concerns: bind address, model file, VAD model, backend and threads. See
///     docs/wiki/24-audio-transcription.md ("whisper-server flags that are deliberately never emitted").
/// </remarks>
internal static class WhisperServerArgumentBuilder
{
    /// <summary>Builds the launch spec for <paramref name="modelId" /> at <paramref name="modelPath" />.</summary>
    internal static WhisperServerLaunchSpec Build(string modelId,
        string modelPath,
        string executablePath,
        WhisperBackend backend,
        int port,
        WhisperRuntimeOptions options,
        int processorCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string>
        {
            "-m",
            modelPath,

            // Loopback bind only — audio never leaves the node.
            "--host",
            options.ListenHost,
            "--port",
            port.ToString(CultureInfo.InvariantCulture)
        };

        // Both VAD flags or neither: -vm without --vad configures a model that is never consulted, and --vad without
        // -vm leaves the server to look for a file nobody told it about.
        if (!string.IsNullOrWhiteSpace(options.VadModelPath))
        {
            args.Add("--vad");
            args.Add("-vm");
            args.Add(options.VadModelPath);
        }

        if (backend == WhisperBackend.Cpu)
        {
            // -ng is only meaningful when a GPU build might otherwise offload; a thread count is only worth pinning
            // when the CPU is doing the work.
            args.Add("-ng");
            args.Add("-t");
            args.Add(ResolveThreadCount(processorCount).ToString(CultureInfo.InvariantCulture));
        }

        // Language probabilities are expensive and the server computes them per request; the per-request field
        // re-enables them for the one call that actually needs a detected language.
        args.Add("-nlp");

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new WhisperServerLaunchSpec
        {
            ModelId = modelId,
            ExecutablePath = executablePath,
            Arguments = args,
            Port = port,
            WorkingDirectory = workingDirectory
        };
    }

    /// <summary>
    ///     Threads for a CPU-backend launch: half the logical cores, bounded to 4..8.
    /// </summary>
    /// <remarks>
    ///     simplified: the CPU thread count is a guess (half the cores, clamped to 4..8). Size it from a real CPU-backend
    ///     run rather than from a second guess.
    /// </remarks>
    internal static int ResolveThreadCount(int processorCount) =>
        Math.Clamp(processorCount / 2, min: 4, max: 8);
}
