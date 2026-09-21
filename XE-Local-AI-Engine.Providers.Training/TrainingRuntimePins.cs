namespace XE_Local_AI_Engine.Providers.Training;

/// <summary>
///     The pinned uv release the training runtime is provisioned with, and the handshake contract it must report.
/// </summary>
/// <remarks>
///     Unlike llama.cpp, whose digests come from the GitHub release-assets API for want of sidecars, uv publishes a
///     <c>.sha256</c> per asset; re-fetch it on a version bump from
///     <c>https://github.com/astral-sh/uv/releases/download/&lt;tag&gt;/&lt;asset&gt;.sha256</c>. There is no OS/arch
///     matrix on purpose: the runtime is Linux-x64-only by gate
///     (<see cref="Contracts.TrainingRuntimePrerequisiteKeys.Platform" />) and the lockfile narrows to the same one.
/// </remarks>
public static class TrainingRuntimePins
{
    public const string UvVersion = "0.12.5";

    public const string UvAssetName = "uv-x86_64-unknown-linux-gnu.tar.gz";

    public const string UvSha256 = "68a509da24b06b4223a1c0175fb5eb5bc79342b76cbeff0cfe51ac3f5b17b6b2";

    /// <summary>The directory the release tarball unpacks into, and the executable inside it.</summary>
    public const string UvArchiveRootDirectory = "uv-x86_64-unknown-linux-gnu";

    public const string UvExecutableName = "uv";

    /// <summary>The handshake version <c>tools/training/probe.py</c> emits.</summary>
    /// <remarks>
    ///     A provisioned runtime whose probe reports a different value is rejected rather than adopted: the scripts and
    ///     the managed side are versioned together, so a mismatch means the two halves are out of step and nothing
    ///     downstream can be trusted.
    /// </remarks>
    public const int ProbeContractVersion = 1;

    public static Uri UvDownloadUri()
    {
        return new Uri($"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{UvAssetName}");
    }
}
