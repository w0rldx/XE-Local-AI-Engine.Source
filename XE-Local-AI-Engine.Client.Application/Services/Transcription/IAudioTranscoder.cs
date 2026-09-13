namespace XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Converts a container the transcription runtime cannot decode natively into 16 kHz mono WAV, engine-side.
/// </summary>
/// <remarks>
///     The runtime daemon is never launched with its own convert flag: that flag writes every request's audio to a
///     temporary file in a directory this engine neither owns nor cleans, which would break the rule that no audio is
///     persisted. Converting here keeps both the input and the output inside the engine-owned temporary directory,
///     where the upload slot deletes them.
/// </remarks>
public interface IAudioTranscoder
{
    /// <summary>
    ///     Whether an <c>ffmpeg</c> executable resolved on <c>PATH</c>. This is the same answer the runtime status
    ///     publishes as its transcode capability, and it decides whether ogg/m4a/webm uploads are accepted at all.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     Transcodes <paramref name="sourcePath" /> into <paramref name="destinationPath" /> as 16 kHz mono WAV and
    ///     returns the destination path.
    /// </summary>
    /// <exception cref="AudioTranscodeException">
    ///     <c>ffmpeg</c> is absent, could not be started, or exited non-zero. The message is sanitized for display.
    /// </exception>
    Task<string> ToWav16kMonoAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);
}
