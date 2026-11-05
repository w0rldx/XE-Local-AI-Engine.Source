namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>A GGUF hashed once for a run of arms, with the file stamp each later load is checked against instead of hashing it again.</summary>
/// <remarks>Accepted ceiling: an edit that restores both the length and the write time goes unseen; re-hashing would cost seconds per arm on a large model.</remarks>
internal sealed class TransientLlamaServerPinnedModel
{
    private readonly string _path;
    private readonly FileStamp _stamp;

    private TransientLlamaServerPinnedModel(string path, FileStamp stamp, TransientLlamaServerModelProvenance provenance)
    {
        _path = path;
        _stamp = stamp;
        Provenance = provenance;
    }

    public TransientLlamaServerModelProvenance Provenance { get; }

    public static async Task<TransientLlamaServerPinnedModel> CaptureAsync(string path, CancellationToken ct)
    {
        // Stamped before hashing, so a write during the hash reads as a change at the next check.
        var stamp = FileStamp.ReadOrNull(path) ?? throw new LlamaRuntimeException("The evaluation model file to load was not found.");
        var provenance = await TransientLlamaServerLauncher.CaptureModelProvenanceAsync(path, adapterPath: null, ct).ConfigureAwait(false);
        return new TransientLlamaServerPinnedModel(path, stamp, provenance);
    }

    public bool IsUnchanged() =>
        FileStamp.ReadOrNull(_path) == _stamp;

    /// <summary>The final target of any symlink: a link's own stamp would not see the file behind it change.</summary>
    private sealed record FileStamp
    {
        public required string TargetPath { get; init; }

        public required long Length { get; init; }

        public required DateTime LastWriteUtc { get; init; }

        public static FileStamp? ReadOrNull(string path)
        {
            var file = new FileInfo(path);
            var target = file.LinkTarget is null ? file : File.ResolveLinkTarget(path, returnFinalTarget: true) as FileInfo;
            return target is { Exists: true }
                ? new FileStamp
                {
                    TargetPath = target.FullName,
                    Length = target.Length,
                    LastWriteUtc = target.LastWriteTimeUtc
                }
                : null;
        }
    }
}
