namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     <see cref="IBaseCheckpointStore" /> over the reused <see cref="HfHubClient" /> + <see cref="HfDownloadClient" />.
///     Mirrors <see cref="HuggingFaceImageModelStore" />: the download primitive is single-file, and everything here is
///     the multi-file orchestration on top of it — per-file staging, reuse of already-complete files, and one
///     set-relative progress bar instead of one bar per shard.
/// </summary>
internal sealed class HuggingFaceBaseCheckpointStore(
    HfHubClient hubClient,
    HfDownloadClient downloadClient,
    ILogger<HuggingFaceBaseCheckpointStore> logger) : IBaseCheckpointStore
{
    private const string DefaultRevision = "main";

    // Exact file names that are always required when present. Anything ending in .safetensors is picked up separately.
    private static readonly string[] ConfigFileNames =
    [
        "config.json",
        "generation_config.json",
        "model.safetensors.index.json"
    ];

    private static readonly string[] TokenizerFileNames =
    [
        "tokenizer.json",
        "tokenizer_config.json",
        "special_tokens_map.json",
        "tokenizer.model",
        "vocab.json",
        "merges.txt",
        "added_tokens.json",
        "chat_template.jinja"
    ];

    private readonly HfDownloadClient _downloadClient = downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));
    private readonly HfHubClient _hubClient = hubClient ?? throw new ArgumentNullException(nameof(hubClient));
    private readonly ILogger<HuggingFaceBaseCheckpointStore> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public async Task<BaseCheckpointManifest> ResolveAsync(string repoId, string? revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        // Read the metadata AT the requested revision. Labelling the default branch's file list with a pinned revision
        // describes a checkpoint that may not exist: the download then fetches those names at the pin, and a file the
        // pin does not have (or has at another size) fails mid-transfer rather than at resolve time.
        var detail = await _hubClient.GetRepoAsync(repoId, revision, ct).ConfigureAwait(false)
                     ?? throw new BaseCheckpointNotTrainableException("The base checkpoint repository could not be read from Hugging Face.");

        var files = new List<BaseCheckpointFile>();
        foreach (var file in detail.Files)
        {
            // Untrusted repo input: a sibling whose name could escape the artifact directory is dropped rather than
            // downloaded, before it ever reaches a path composition.
            if (!GgufFilePath.IsSafeRelativePath(file.FileName))
            {
                _logger.LogWarning("Skipping a base-checkpoint file with an unsafe name in repository {RepoId}.", repoId);
                continue;
            }

            var role = ClassifyFile(file.FileName);
            if (role is null)
            {
                continue;
            }

            files.Add(new BaseCheckpointFile
            {
                Role = role.Value,
                FileName = file.FileName,
                SizeBytes = file.SizeBytes,
                Sha256 = file.Sha256
            });
        }

        if (!files.Exists(static file => file.Role == BaseCheckpointFileRole.Weights))
        {
            // A GGUF-only repo is the common case here: it has a config and a tokenizer but nothing trainable.
            throw new BaseCheckpointNotTrainableException(
                "The selected repository has no safetensors weights, so it cannot be fine-tuned. Choose the original base checkpoint repository rather than a quantized copy.");
        }

        return new BaseCheckpointManifest
        {
            RepoId = detail.RepoId,
            Revision = string.IsNullOrWhiteSpace(revision)
                ? detail.Revision
                : revision,
            Files = files,
            TotalBytes = files.Sum(static file => file.SizeBytes),
            License = detail.License,
            IsGated = detail.IsGated
        };
    }

    /// <inheritdoc />
    public async Task<BaseCheckpointManifest> DownloadAsync(BaseCheckpointManifest manifest,
        string destinationDirectory,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var revision = string.IsNullOrWhiteSpace(manifest.Revision) ? DefaultRevision : manifest.Revision;

        // A set total is only honest when every file declares a size; summing the known ones would report a total the
        // transfer overshoots, and a bar that passes 100% is worse than one that admits it cannot compute a percentage.
        long? knownSetTotal = manifest.Files.All(static file => file.SizeBytes > 0)
            ? manifest.Files.Sum(static file => file.SizeBytes)
            : null;

        var downloaded = new List<BaseCheckpointFile>(manifest.Files.Count);
        long completedBytes = 0;
        var index = 0;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            index++;

            var destinationPath = GgufFilePath.ResolveContainedPath(destinationDirectory, file.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            // Reuse a file this artifact already has. Without it, a set that failed on its last shard re-downloads every
            // earlier shard on the next attempt — tens of gigabytes of pointless transfer for a large checkpoint.
            if (TryReuseCompletedFile(destinationPath, file, out var reusedSize))
            {
                completedBytes += reusedSize;
                downloaded.Add(file with
                {
                    LocalPath = destinationPath,
                    SizeBytes = reusedSize
                });
                continue;
            }

            var fileProgress = progress is null
                ? null
                : new SetProgressAdapter(progress, manifest.RepoId, completedBytes, knownSetTotal, index, manifest.Files.Count);

            var result = await _downloadClient.DownloadAsync(manifest.RepoId,
                file.FileName,
                revision,
                manifest.RepoId,
                destinationPath,
                file.SizeBytes,
                file.Sha256,
                fileProgress,
                ct).ConfigureAwait(false);

            completedBytes += result.SizeBytes;
            downloaded.Add(file with
            {
                LocalPath = result.LocalPath,
                SizeBytes = result.SizeBytes,
                // Only the verified hash — the discovery digest we passed was used for verification, never echoed.
                Sha256 = result.Sha256
            });
        }

        return manifest with
        {
            Files = downloaded,
            TotalBytes = completedBytes
        };
    }

    /// <summary>
    ///     Decides what a repo file is for, or <see langword="null" /> when fine-tuning does not need it. Everything not
    ///     recognised is skipped on purpose: a base repo commonly also ships ONNX exports, GGUF quants, PyTorch <c>.bin</c>
    ///     duplicates of the same weights, and images, and downloading those would multiply the transfer for nothing.
    /// </summary>
    internal static BaseCheckpointFileRole? ClassifyFile(string fileName)
    {
        // Only root-level files are considered: subdirectories in these repos hold the alternative formats above.
        if (fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            return BaseCheckpointFileRole.Weights;
        }

        if (Array.Exists(ConfigFileNames, name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return BaseCheckpointFileRole.Config;
        }

        return Array.Exists(TokenizerFileNames, name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
            ? BaseCheckpointFileRole.Tokenizer
            : null;
    }

    /// <summary>
    ///     Reuse requires a declared size that matches the file exactly: without one there is nothing to check a leftover
    ///     against, and a truncated file would be indistinguishable from a complete one. Length is deliberately the only
    ///     check — hashing a 30 GB shard to avoid re-downloading it costs a large fraction of the transfer it saves, and
    ///     anything actually fetched is still hash-verified by the download client.
    /// </summary>
    private static bool TryReuseCompletedFile(string destinationPath, BaseCheckpointFile file, out long sizeBytes)
    {
        sizeBytes = 0;
        if (file.SizeBytes <= 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(destinationPath);
            if (!info.Exists || info.Length != file.SizeBytes)
            {
                return false;
            }

            sizeBytes = info.Length;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Re-frames one file's byte counts as progress through the whole checkpoint, so a sharded model shows one
    ///     monotonic bar instead of a bar that fills and snaps back to zero once per shard.
    /// </summary>
    private sealed class SetProgressAdapter(
        IProgress<PullProgress> inner,
        string modelName,
        long completedInPriorFiles,
        long? setTotalBytes,
        int partIndex,
        int partCount) : IProgress<PullProgress>
    {
        public void Report(PullProgress value)
        {
            inner.Report(new PullProgress
            {
                ModelName = modelName,
                Status = value.Status,
                TotalBytes = setTotalBytes ?? value.TotalBytes,
                CompletedBytes = completedInPriorFiles + (value.CompletedBytes ?? 0),
                PartIndex = partIndex,
                PartCount = partCount
            });
        }
    }
}
