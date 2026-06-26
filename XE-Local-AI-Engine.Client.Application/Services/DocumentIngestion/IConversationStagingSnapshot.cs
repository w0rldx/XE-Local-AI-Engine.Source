namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     A short-lived directory of DECRYPTED per-file extracted Markdown for one conversation, ready for the AgentHome
///     staging step to copy into the sandbox via its guarded copy-in path. Disposing removes the directory; callers
///     must dispose (ideally with <c>await using</c>) so plaintext never lingers on disk.
/// </summary>
public interface IConversationStagingSnapshot : IAsyncDisposable
{
    /// <summary>Absolute path to the temp directory holding the decrypted <c>.md</c> attachment files.</summary>
    string HostPath { get; }

    /// <summary>Number of decrypted attachment files written into <see cref="HostPath"/>.</summary>
    int FileCount { get; }
}
