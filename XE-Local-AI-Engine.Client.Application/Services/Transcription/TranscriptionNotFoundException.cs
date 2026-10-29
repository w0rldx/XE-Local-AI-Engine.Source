namespace XE_Local_AI_Engine.Client.Services.Transcription;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Identifies a missing transcription session, or a transcript row the session does not hold, at the
///     <see cref="ITranscriptionService" /> boundary.
/// </summary>
/// <remarks>
///     <c>ResourceNotFoundExceptionHandler</c> answers it with the surface's bodyless 404, so the Transcription endpoints
///     do not branch on a null lookup themselves. <c>TranscriptionHub</c> keeps its own null check on the summary read,
///     because a persist-free dictation session has no row and is still legitimate there.
/// </remarks>
public sealed class TranscriptionNotFoundException : ResourceNotFoundException
{
    public TranscriptionNotFoundException(string message) : base(message)
    {
    }
}
