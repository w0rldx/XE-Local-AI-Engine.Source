namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>Streams one explicit prompt to one model and persists nothing.</summary>
public interface IDirectInferenceService
{
    /// <summary>Validates <paramref name="request" /> eagerly, then streams its events; the last one is <c>done</c> or <c>error</c>.</summary>
    /// <exception cref="DirectInferenceInvalidRequestException">The request is incomplete or a sampling value is out of range.</exception>
    IAsyncEnumerable<DirectInferenceEvent> StreamAsync(DirectInferenceRequest request, CancellationToken cancellationToken);
}

/// <summary>A Direct Inference request the caller got wrong; the message names every problem and nothing internal.</summary>
public sealed class DirectInferenceInvalidRequestException : Exception
{
    public DirectInferenceInvalidRequestException()
    {
    }

    public DirectInferenceInvalidRequestException(string message)
        : base(message)
    {
    }

    public DirectInferenceInvalidRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
