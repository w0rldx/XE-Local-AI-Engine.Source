namespace XE_Local_AI_Engine.Cli.Discovery;

/// <summary>No usable node was found; <see cref="Failure" /> says why and the message is fit to print.</summary>
public sealed class NodeDiscoveryException : Exception
{
    public NodeDiscoveryException(NodeDiscoveryFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public NodeDiscoveryFailure Failure { get; }
}
