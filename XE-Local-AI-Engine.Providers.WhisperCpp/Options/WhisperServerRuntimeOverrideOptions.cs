namespace XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Operator "bring-your-own" <c>whisper-server</c> override pointing the runtime at a locally-built binary — a Linux
///     CUDA build, for which upstream ships no prebuilt asset — instead of the pinned download-and-verify path.
/// </summary>
/// <remarks>
///     Off by default: with <see cref="ServerPath" /> unset the selector and the binary manager behave byte-identically to the pinned
///     path. <b>Trust-channel containment:</b> the override is <em>operator-trust only</em>, built exclusively from process environment
///     variables (<see cref="ServerPathEnvironmentVariable" /> / <see cref="BackendEnvironmentVariable" />) via
///     <see cref="FromEnvironment" /> and NEVER from configuration, node settings or a request DTO. See
///     docs/wiki/24-audio-transcription.md ("The bring-your-own whisper-server override").
/// </remarks>
public sealed class WhisperServerRuntimeOverrideOptions
{
    /// <summary>Process environment variable holding the absolute path to the operator-supplied <c>whisper-server</c>.</summary>
    public const string ServerPathEnvironmentVariable = "XE_WHISPERCPP_SERVER_PATH";

    /// <summary>
    ///     Process environment variable selecting the acceleration backend the override binary was built for
    ///     (<c>cpu</c>/<c>cuda</c>, case-insensitive). Defaults to <see cref="WhisperBackend.Cuda" /> when unset.
    /// </summary>
    public const string BackendEnvironmentVariable = "XE_WHISPERCPP_BACKEND";

    /// <summary>Absolute path to the operator-supplied <c>whisper-server</c>; <see langword="null" /> when unset.</summary>
    public string? ServerPath { get; init; }

    /// <summary>
    ///     The acceleration backend the override binary claims. Defaults to <see cref="WhisperBackend.Cuda" /> — the
    ///     primary bring-your-own case is a Linux CUDA build, which has no prebuilt asset.
    /// </summary>
    public WhisperBackend Backend { get; init; } = WhisperBackend.Cuda;

    /// <summary>True when an override path is configured; the single signal both the selector and the manager key off.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(ServerPath);

    /// <summary>
    ///     Builds the override from process environment variables only (the operator-trust channel). Reads
    ///     <see cref="ServerPathEnvironmentVariable" /> and <see cref="BackendEnvironmentVariable" /> via explicit
    ///     <see cref="Environment.GetEnvironmentVariable(string)" /> — matching the repo's <c>XE_*</c> convention — NOT
    ///     .NET section binding.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when <see cref="BackendEnvironmentVariable" /> is set to a value that is not a recognized backend. A
    ///     set-but-unparseable backend is a startup misconfiguration and fails fast rather than silently defaulting.
    /// </exception>
    public static WhisperServerRuntimeOverrideOptions FromEnvironment()
    {
        var serverPath = Environment.GetEnvironmentVariable(ServerPathEnvironmentVariable);
        var rawBackend = Environment.GetEnvironmentVariable(BackendEnvironmentVariable);

        return new WhisperServerRuntimeOverrideOptions
        {
            ServerPath = string.IsNullOrWhiteSpace(serverPath) ? null : serverPath.Trim(),
            Backend = ParseBackend(rawBackend)
        };
    }

    /// <summary>
    ///     Parses the backend token case-insensitively. An unset or blank value defaults to
    ///     <see cref="WhisperBackend.Cuda" />; a non-blank value matching no known backend is rejected.
    /// </summary>
    private static WhisperBackend ParseBackend(string? rawBackend)
    {
        if (string.IsNullOrWhiteSpace(rawBackend))
        {
            return WhisperBackend.Cuda;
        }

        var token = rawBackend.Trim();
        if (string.Equals(token, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return WhisperBackend.Cuda;
        }

        if (string.Equals(token, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return WhisperBackend.Cpu;
        }

        throw new InvalidOperationException($"The environment variable '{BackendEnvironmentVariable}' is set to an unrecognized whisper.cpp acceleration backend. Use one of: cpu, cuda.");
    }
}
