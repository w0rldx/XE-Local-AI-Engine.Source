namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     A staging snapshot backed by a temp directory of decrypted <c>.md</c> attachments. Disposal removes the
///     directory so the plaintext copy does not outlive the agent run that consumed it.
/// </summary>
internal sealed class ConversationStagingSnapshot : IConversationStagingSnapshot
{
    public ConversationStagingSnapshot(string hostPath, IReadOnlyList<string> fileNames)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentNullException.ThrowIfNull(fileNames);

        HostPath = hostPath;
        FileNames = fileNames;
    }

    public string HostPath { get; }

    public int FileCount => FileNames.Count;

    public IReadOnlyList<string> FileNames { get; }

    public ValueTask DisposeAsync()
    {
        // Best-effort cleanup: a missing or partly-removed directory is not an error worth surfacing to the run.
        try
        {
            if (Directory.Exists(HostPath))
            {
                Directory.Delete(HostPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a transient IO error here is not worth surfacing to the agent run.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a permission error here is not worth surfacing to the agent run.
        }

        return ValueTask.CompletedTask;
    }
}
