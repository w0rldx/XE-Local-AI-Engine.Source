namespace XE_Local_AI_Engine.Providers.Training;

/// <summary>
///     The pinned uv release the training runtime is provisioned with, and the handshake contract it must report.
/// </summary>
/// <remarks>
///     <para>
///         Unlike llama.cpp — which publishes no <c>.sha256</c> sidecars, so its digests come from the GitHub
///         release-assets API — uv publishes a <c>.sha256</c> per asset. Re-fetch on a version bump with:
///         <c>curl -sL https://github.com/astral-sh/uv/releases/download/&lt;tag&gt;/&lt;asset&gt;.sha256</c>.
///         Verified live 2026-08-15 against 0.12.5.
///     </para>
///     <para>
///         There is no OS/arch matrix here on purpose. The runtime is Linux-x64-only by gate
///         (<see cref="Contracts.TrainingRuntimePrerequisiteKeys.Platform" />), and the committed lockfile narrows
///         resolution to the same platform, so a second pin would only be a way to disagree with itself.
///     </para>
/// </remarks>
public static class TrainingRuntimePins
{
    public const string UvVersion = "0.12.5";

    public const string UvAssetName = "uv-x86_64-unknown-linux-gnu.tar.gz";

    public const string UvSha256 = "68a509da24b06b4223a1c0175fb5eb5bc79342b76cbeff0cfe51ac3f5b17b6b2";

    /// <summary>The directory the release tarball unpacks into, and the executable inside it.</summary>
    public const string UvArchiveRootDirectory = "uv-x86_64-unknown-linux-gnu";

    public const string UvExecutableName = "uv";

    /// <summary>
    ///     The handshake version <c>tools/training/probe.py</c> emits. A provisioned runtime whose probe reports a
    ///     different value is rejected rather than adopted: the scripts and the managed side are versioned together, so a
    ///     mismatch means the two halves are out of step and nothing downstream can be trusted.
    /// </summary>
    public const int ProbeContractVersion = 1;

    public static Uri UvDownloadUri()
    {
        return new Uri($"https://github.com/astral-sh/uv/releases/download/{UvVersion}/{UvAssetName}");
    }
}
