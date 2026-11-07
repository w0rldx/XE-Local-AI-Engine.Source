namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>What a required base-checkpoint file is for. Drives both the UI and the "is this repo trainable" check.</summary>
public enum BaseCheckpointFileRole
{
    /// <summary>An architecture/config document (<c>config.json</c>, <c>generation_config.json</c>).</summary>
    Config = 0,

    /// <summary>A tokenizer artifact (<c>tokenizer.json</c>, <c>tokenizer_config.json</c>, <c>*.model</c>, …).</summary>
    Tokenizer = 1,

    /// <summary>A weight shard (<c>*.safetensors</c>) or its index.</summary>
    Weights = 2
}

/// <summary>One required file of a base checkpoint, in the image lane's parts shape.</summary>
public sealed record BaseCheckpointFile
{
    public required BaseCheckpointFileRole Role { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public string? Sha256 { get; init; }

    /// <summary>Absolute local path once downloaded; empty while the file is only enumerated.</summary>
    public string LocalPath { get; init; } = string.Empty;
}

/// <summary>
///     The enumerated file set of a trainable checkpoint plus the licensing facts recorded alongside it. The license is
///     read from <b>this</b> repo — the base checkpoint — never from a GGUF quant repo derived from it.
/// </summary>
public sealed record BaseCheckpointManifest
{
    public required string RepoId { get; init; }

    /// <summary>The commit sha the Hub resolved the request to, never a branch or tag name.</summary>
    public required string Revision { get; init; }

    public required IReadOnlyList<BaseCheckpointFile> Files { get; init; }

    public required long TotalBytes { get; init; }

    /// <summary>The <c>cardData.license</c> tag, or <see langword="null" /> when the repo declares none.</summary>
    public string? License { get; init; }

    /// <summary>True when the Hub reports the repo as gated ("auto" or "manual" access).</summary>
    public required bool IsGated { get; init; }
}

/// <summary>
///     Resolves and downloads trainable Hugging Face base checkpoints (safetensors + config + tokenizer).
/// </summary>
/// <remarks>
///     Sits beside the GGUF and image stores over the same <c>HfHubClient</c> / <c>HfDownloadClient</c> pair, because
///     those are internal to this assembly and multi-file download is orchestration over them rather than a new
///     primitive.
/// </remarks>
public interface IBaseCheckpointStore
{
    /// <summary>
    ///     Enumerates the files required to fine-tune <paramref name="repoId" /> and reads its licensing metadata.
    /// </summary>
    /// <exception cref="BaseCheckpointNotTrainableException">
    ///     The repo does not exist, the Hub reported no commit for it, or it ships no <c>*.safetensors</c> weights — a
    ///     GGUF-only or otherwise non-trainable repo (there is no attestation workaround).
    /// </exception>
    Task<BaseCheckpointManifest> ResolveAsync(string repoId, string? revision, CancellationToken ct);

    /// <summary>
    ///     Downloads every file of <paramref name="manifest" /> into <paramref name="destinationDirectory" />, one file
    ///     at a time with <c>.part</c> staging, resume and SHA-256 verification, and returns the manifest with each
    ///     file's verified local path filled in.
    /// </summary>
    /// <remarks>
    ///     Already-complete files are reused rather than refetched, so a resumed download does not re-transfer finished
    ///     shards.
    /// </remarks>
    Task<BaseCheckpointManifest> DownloadAsync(BaseCheckpointManifest manifest,
        string destinationDirectory,
        IProgress<PullProgress>? progress,
        CancellationToken ct);

    /// <summary>
    ///     Resolves <paramref name="repoId" /> to its commit and downloads only its <c>config.json</c> into
    ///     <paramref name="destinationDirectory" />. Unlike <see cref="ResolveAsync" /> it needs no safetensors.
    /// </summary>
    /// <returns>A manifest naming the resolved commit, with the one config file and its local path.</returns>
    /// <exception cref="BaseCheckpointConfigException">The repo could not be read, has no config, or its config is over <see cref="BaseCheckpointConfigException.MaxConfigBytes" />.</exception>
    /// <exception cref="XE_Local_AI_Engine.Providers.Abstractions.Gguf.HuggingFaceDownloadException">The transfer failed; a gated repo without access lands here.</exception>
    Task<BaseCheckpointManifest> FetchConfigAsync(string repoId, string? revision, string destinationDirectory, CancellationToken ct);
}

/// <summary>Why <see cref="IBaseCheckpointStore.FetchConfigAsync" /> refused before any transfer.</summary>
public enum BaseCheckpointConfigFailure
{
    Unreachable,
    ConfigMissing,
    ConfigTooLarge
}

/// <summary>A config-only fetch refused before any transfer. The message is operator-facing.</summary>
public sealed class BaseCheckpointConfigException : Exception
{
    /// <summary>A <c>config.json</c> describes a model's shape; anything larger is not one.</summary>
    public const long MaxConfigBytes = 1024 * 1024;

    public BaseCheckpointConfigException()
    {
    }

    public BaseCheckpointConfigException(string message)
        : base(message)
    {
    }

    public BaseCheckpointConfigException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public BaseCheckpointConfigException(BaseCheckpointConfigFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public BaseCheckpointConfigFailure Failure { get; }
}

/// <summary>The selected repository cannot be fine-tuned from. The message is operator-facing.</summary>
public sealed class BaseCheckpointNotTrainableException : Exception
{
    public BaseCheckpointNotTrainableException()
    {
    }

    public BaseCheckpointNotTrainableException(string message)
        : base(message)
    {
    }

    public BaseCheckpointNotTrainableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
