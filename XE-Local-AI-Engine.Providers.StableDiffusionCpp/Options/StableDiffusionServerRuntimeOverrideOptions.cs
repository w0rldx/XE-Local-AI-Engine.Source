namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Operator "bring-your-own" <c>sd-server</c> override pointing the runtime at a locally-built binary — a Linux CUDA
///     build, say, for which no prebuilt asset is shipped — instead of the pinned download-and-verify path.
/// </summary>
/// <remarks>
///     Off by default: when <see cref="ServerPath" /> is unset the selector and binary manager behave byte-identically to the pinned
///     path. <b>Trust-channel containment:</b> the override is <em>operator-trust only</em>, built exclusively from process environment
///     variables (<see cref="ServerPathEnvironmentVariable" /> / <see cref="BackendEnvironmentVariable" />) via
///     <see cref="FromEnvironment" /> and NEVER from configuration, node settings or a request DTO. See
///     docs/wiki/14-image-generation.md ("The bring-your-own sd-server override").
/// </remarks>
public sealed class StableDiffusionServerRuntimeOverrideOptions
{
    /// <summary>Process environment variable holding the absolute path to the operator-supplied <c>sd-server</c>.</summary>
    public const string ServerPathEnvironmentVariable = "XE_SDCPP_SERVER_PATH";

    /// <summary>
    ///     Process environment variable selecting the acceleration backend the override binary was built for
    ///     (<c>cpu</c>/<c>cuda</c>/<c>vulkan</c>, case-insensitive). Defaults to <see cref="SdGpuBackend.Cuda" /> when unset.
    /// </summary>
    public const string BackendEnvironmentVariable = "XE_SDCPP_BACKEND";

    /// <summary>Absolute path to the operator-supplied <c>sd-server</c>; <see langword="null" /> when no override is set.</summary>
    public string? ServerPath { get; init; }

    /// <summary>
    ///     The acceleration backend the override binary claims. Defaults to <see cref="SdGpuBackend.Cuda" /> — the primary
    ///     bring-your-own use case (a Linux CUDA build, which has no prebuilt asset).
    /// </summary>
    public SdGpuBackend Backend { get; init; } = SdGpuBackend.Cuda;

    /// <summary>True when an override path is configured; the single signal both the selector and the manager key off.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(ServerPath);

    /// <summary>
    ///     Builds the override from process environment variables only (operator-trust channel). Reads
    ///     <see cref="ServerPathEnvironmentVariable" /> and <see cref="BackendEnvironmentVariable" /> via explicit
    ///     <see cref="Environment.GetEnvironmentVariable(string)" /> — matching the repo <c>XE_*</c> convention — NOT .NET
    ///     section binding.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when <see cref="BackendEnvironmentVariable" /> is set to a value that is not a recognized backend. A
    ///     set-but-unparseable backend is a startup misconfiguration and fails fast rather than silently defaulting.
    /// </exception>
    public static StableDiffusionServerRuntimeOverrideOptions FromEnvironment()
    {
        var serverPath = Environment.GetEnvironmentVariable(ServerPathEnvironmentVariable);
        var rawBackend = Environment.GetEnvironmentVariable(BackendEnvironmentVariable);

        return new StableDiffusionServerRuntimeOverrideOptions
        {
            ServerPath = string.IsNullOrWhiteSpace(serverPath) ? null : serverPath.Trim(),
            Backend = ParseBackend(rawBackend)
        };
    }

    /// <summary>
    ///     Parses the backend token case-insensitively. An unset/blank value defaults to <see cref="SdGpuBackend.Cuda" />
    ///     (the primary bring-your-own case); a non-blank value that matches no known backend is rejected.
    /// </summary>
    private static SdGpuBackend ParseBackend(string? rawBackend)
    {
        if (string.IsNullOrWhiteSpace(rawBackend))
        {
            return SdGpuBackend.Cuda;
        }

        var token = rawBackend.Trim();
        if (string.Equals(token, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return SdGpuBackend.Cuda;
        }

        if (string.Equals(token, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return SdGpuBackend.Vulkan;
        }

        if (string.Equals(token, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return SdGpuBackend.Cpu;
        }

        throw new InvalidOperationException(
            $"The environment variable '{BackendEnvironmentVariable}' is set to an unrecognized stable-diffusion.cpp acceleration backend. Use one of: cpu, cuda, vulkan.");
    }
}
