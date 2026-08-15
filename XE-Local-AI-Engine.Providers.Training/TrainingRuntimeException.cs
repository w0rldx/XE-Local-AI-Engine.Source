namespace XE_Local_AI_Engine.Providers.Training;

/// <summary>
///     A training-runtime failure whose message is user-safe <b>by contract</b>: every construction site phrases it for
///     an operator and names no path, URL, token, or environment value. The phase machine surfaces these verbatim as
///     the sanitized error and collapses every other exception to a generic reason, so widening that guarantee here
///     silently widens what leaks to the UI.
/// </summary>
public sealed class TrainingRuntimeException : Exception
{
    public TrainingRuntimeException()
    {
    }

    public TrainingRuntimeException(string message)
        : base(message)
    {
    }

    public TrainingRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
