namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Thrown when an operation targets a sandbox that was killed, never created, or whose
///     <see cref="SandboxAttachKey" /> does not match a live sandbox. Lets callers (and tests) distinguish an invalid
///     handle from a genuine command failure.
/// </summary>
public sealed class SandboxHandleInvalidException : InvalidOperationException
{
    public SandboxHandleInvalidException()
    {
    }

    public SandboxHandleInvalidException(string message) : base(message)
    {
    }

    public SandboxHandleInvalidException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
