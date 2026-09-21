namespace XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     A ready <c>whisper-server</c> daemon endpoint: which model it currently serves, at which process generation, and
///     the loopback root the transcriber posts to.
/// </summary>
public sealed record WhisperServerEndpoint
{
    /// <summary>The catalogue id of the model currently loaded.</summary>
    public required string ModelId { get; init; }

    /// <summary>
    ///     Monotonic process-generation counter, incremented on every spawn AND on every successful in-place model load.
    /// </summary>
    /// <remarks>
    ///     It is what makes a transcription lease unambiguous: this daemon is mutable, so the model id alone cannot say
    ///     whether the instance a caller resolved is still the instance it is about to use.
    /// </remarks>
    public required long Generation { get; init; }

    /// <summary>The server root, <c>http://127.0.0.1:{port}/</c>.</summary>
    public required Uri BaseAddress { get; init; }
}
