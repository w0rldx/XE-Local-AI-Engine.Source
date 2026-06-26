namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     In-memory <see cref="IConversationUploadedFileStore" /> for AgentHome staging tests. Tracks the metadata rows per
///     conversation and, when <see cref="CreateStagingSnapshotAsync" /> is called, writes the configured decrypted
///     Markdown into a fresh temp directory whose disposal removes it — mirroring the real store's staging contract
///     without any encryption or persistence. Only the read/list/staging members the staging step uses are implemented.
/// </summary>
public sealed class FakeConversationUploadedFileStore : IConversationUploadedFileStore
{
    private readonly Dictionary<Guid, List<StagedFile>> _filesByConversation = [];

    /// <summary>The host paths of every staging snapshot created, so a test can assert disposal removed them.</summary>
    public List<string> CreatedSnapshotPaths { get; } = [];

    /// <summary>Adds one file (metadata + the decrypted Markdown a staging snapshot would write) for a conversation.</summary>
    public void Add(Guid conversationId, string originalFileName, string extractedMarkdown)
    {
        if (!_filesByConversation.TryGetValue(conversationId, out var files))
        {
            files = [];
            _filesByConversation[conversationId] = files;
        }

        var fileId = Guid.NewGuid();
        files.Add(new StagedFile(
            new ConversationUploadedFileInfo(
                fileId,
                conversationId,
                originalFileName,
                "text/markdown",
                ".md",
                extractedMarkdown.Length,
                DocumentExtractionStatus.Extracted,
                extractedMarkdown.Length,
                CreatedAtUtc: 0),
            extractedMarkdown));
    }

    public Task<ConversationUploadedFileInfo> AddAsync(ConversationUploadedFileInput input, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<IReadOnlyList<ConversationUploadedFileInfo>> ListAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ConversationUploadedFileInfo> infos = _filesByConversation.TryGetValue(conversationId, out var files)
            ? files.Select(file => file.Info).ToList()
            : [];
        return Task.FromResult(infos);
    }

    public Task<string?> ReadExtractedMarkdownAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        var markdown = _filesByConversation.TryGetValue(conversationId, out var files)
            ? files.FirstOrDefault(file => file.Info.FileId == fileId)?.Markdown
            : null;
        return Task.FromResult(markdown);
    }

    public Task<bool> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task DeleteAllForConversationAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public async Task<IConversationStagingSnapshot> CreateStagingSnapshotAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var hostPath = Path.Combine(Path.GetTempPath(), "fake-attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(hostPath);
        CreatedSnapshotPaths.Add(hostPath);

        var files = _filesByConversation.TryGetValue(conversationId, out var staged) ? staged : [];
        foreach (var file in files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Info.OriginalFileName) + ".md";
            await File.WriteAllTextAsync(Path.Combine(hostPath, fileName), file.Markdown, cancellationToken).ConfigureAwait(false);
        }

        return new FakeStagingSnapshot(hostPath, files.Count);
    }

    private sealed record StagedFile(ConversationUploadedFileInfo Info, string Markdown);

    private sealed class FakeStagingSnapshot : IConversationStagingSnapshot
    {
        public FakeStagingSnapshot(string hostPath, int fileCount)
        {
            HostPath = hostPath;
            FileCount = fileCount;
        }

        public string HostPath { get; }

        public int FileCount { get; }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(HostPath))
            {
                Directory.Delete(HostPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
