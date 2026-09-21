namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Operator "bring-your-own" llama-server override, pointing the runtime at a locally-built <c>llama-server</c>
///     instead of the pinned download-and-verify acquisition path. Off by default.
/// </summary>
/// <remarks>
///     Operator-trust ONLY — the same trust level as the app binary itself: built exclusively from process environment
///     variables via <see cref="FromEnvironment" />, NEVER from an <c>IConfiguration</c> section, the user-editable node
///     settings store or a request DTO. Intentionally dumb besides, deciding only WHETHER an override is configured and
///     WHICH variant it claims and doing no I/O; the binary manager validates the path at acquisition. Why the
///     containment licenses skipping the SHA256 pin: docs/wiki/03-local-runtime-and-providers.md, "Binary manager + dynamic updater".
/// </remarks>
public sealed class LlamaServerRuntimeOverrideOptions
{
    /// <summary>Process environment variable holding the absolute path to the operator-supplied <c>llama-server</c>.</summary>
    public const string ServerPathEnvironmentVariable = "XE_LLAMACPP_SERVER_PATH";

    /// <summary>
    ///     Process environment variable selecting the acceleration variant the override binary was built for
    ///     (<c>cpu</c>/<c>cuda</c>/<c>vulkan</c>, case-insensitive). Defaults to <see cref="GpuVariant.Cuda" /> when unset.
    /// </summary>
    public const string VariantEnvironmentVariable = "XE_LLAMACPP_VARIANT";

    /// <summary>Absolute path to the operator-supplied <c>llama-server</c>; <see langword="null" /> when no override is set.</summary>
    public string? ServerPath { get; init; }

    /// <summary>
    ///     The acceleration variant the override binary claims. Defaults to <see cref="GpuVariant.Cuda" /> — the primary
    ///     bring-your-own use case (a Linux CUDA build) — and is verified against the binary's reported devices at
    ///     acquisition time when it is not <see cref="GpuVariant.Cpu" />.
    /// </summary>
    public GpuVariant Variant { get; init; } = GpuVariant.Cuda;

    /// <summary>True when an override path is configured; the single signal both the selector and the manager key off.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(ServerPath);

    /// <summary>
    ///     Builds the override from process environment variables only (operator-trust channel). Reads
    ///     <see cref="ServerPathEnvironmentVariable" /> and <see cref="VariantEnvironmentVariable" /> via explicit
    ///     <see cref="Environment.GetEnvironmentVariable(string)" /> — matching the repo <c>XE_*</c> convention
    ///     (for example <c>XE_NODE_SQLITE_KEY</c>, <c>XE_LAUNCH_MODE</c>) — NOT .NET section binding.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when <see cref="VariantEnvironmentVariable" /> is set to a value that is not a recognized variant. A
    ///     set-but-unparseable variant is a startup misconfiguration and fails fast rather than silently defaulting.
    /// </exception>
    public static LlamaServerRuntimeOverrideOptions FromEnvironment()
    {
        var serverPath = Environment.GetEnvironmentVariable(ServerPathEnvironmentVariable);
        var rawVariant = Environment.GetEnvironmentVariable(VariantEnvironmentVariable);

        return new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = string.IsNullOrWhiteSpace(serverPath) ? null : serverPath.Trim(),
            Variant = ParseVariant(rawVariant)
        };
    }

    /// <summary>
    ///     Parses the variant token case-insensitively. An unset/blank value defaults to <see cref="GpuVariant.Cuda" />
    ///     (the primary bring-your-own case); a non-blank value that matches no known variant is rejected.
    /// </summary>
    private static GpuVariant ParseVariant(string? rawVariant)
    {
        if (string.IsNullOrWhiteSpace(rawVariant))
        {
            return GpuVariant.Cuda;
        }

        var token = rawVariant.Trim();
        if (string.Equals(token, "cuda", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVariant.Cuda;
        }

        if (string.Equals(token, "vulkan", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVariant.Vulkan;
        }

        if (string.Equals(token, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVariant.Cpu;
        }

        throw new InvalidOperationException($"The environment variable '{VariantEnvironmentVariable}' is set to an unrecognized llama.cpp acceleration variant. Use one of: cpu, cuda, vulkan.");
    }
}
