namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>A container operation is impermissible because of the state the daemon is in, not because of the argument it was given.</summary>
/// <remarks>
///     A caller bug — a non-loopback host IP, a tag instead of a digest, a blank user — is refused with an
///     <see cref="ArgumentException" /> before anything reaches the wire; this is a well-formed request that what
///     the daemon already holds makes unsafe (reusing a network whose name matches but whose labels do not prove it
///     is ours). Deliberately NOT a <c>DockerRuntimeException</c>: that carries a Development Mode preflight status
///     with no member for a policy refusal, and widening it would put the two consumers back in one enum.
/// </remarks>
public sealed class ContainerPolicyException : Exception
{
    /// <summary>The stable machine token for a network name held by something this instance does not own.</summary>
    public const string ForeignNetworkReason = "ForeignNetwork";

    public ContainerPolicyException(string reason, string detail) : base(detail)
    {
        Reason = reason;
        Detail = detail;
    }

    public ContainerPolicyException(string reason, string detail, Exception innerException) : base(detail, innerException)
    {
        Reason = reason;
        Detail = detail;
    }

    public ContainerPolicyException(string message) : base(message)
    {
        Reason = string.Empty;
        Detail = message;
    }

    public ContainerPolicyException(string message, Exception innerException) : base(message, innerException)
    {
        Reason = string.Empty;
        Detail = message;
    }

    public ContainerPolicyException()
    {
        Reason = string.Empty;
        Detail = string.Empty;
    }

    /// <summary>
    ///     A stable machine token for the refusal, so a caller branches on the reason rather than on the prose.
    ///     Empty only when the exception was constructed through a standard message-only constructor.
    /// </summary>
    public string Reason { get; }

    /// <summary>The operator prose. Same text as the message; named so a caller does not have to know that.</summary>
    public string Detail { get; }
}
