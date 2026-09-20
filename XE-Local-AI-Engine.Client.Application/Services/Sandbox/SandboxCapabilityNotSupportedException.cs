namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Thrown when a <see cref="SandboxCreateRequest" /> asks a provider to enforce an isolation guarantee it does not implement.
/// </summary>
/// <remarks>
///     The provider FAILS CLOSED: it rejects the request rather than silently ignoring the unenforceable guarantee and handing back a
///     sandbox weaker than the caller asked for. Callers and tests use this to distinguish an unenforceable-capability rejection from an
///     invalid handle or a command failure.
/// </remarks>
public sealed class SandboxCapabilityNotSupportedException : InvalidOperationException
{
    public SandboxCapabilityNotSupportedException()
    {
    }

    public SandboxCapabilityNotSupportedException(string message) : base(message)
    {
    }

    public SandboxCapabilityNotSupportedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
