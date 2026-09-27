namespace XE_Local_AI_Engine.Providers.Python;

/// <summary>
///     A uv or Python-tool failure whose message is user-safe <b>by contract</b>: every construction site phrases it
///     for an operator and names no path, URL, token or environment value.
/// </summary>
/// <remarks>
///     The same contract as <c>TrainingRuntimeException</c>, whose catch sites surface both verbatim; the wording stays
///     neutral because the training runtime and the compute tool alike provision through this project.
/// </remarks>
public sealed class ManagedPythonException : Exception
{
    public ManagedPythonException()
    {
    }

    public ManagedPythonException(string message)
        : base(message)
    {
    }

    public ManagedPythonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
