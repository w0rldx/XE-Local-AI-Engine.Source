namespace XE_Local_AI_Engine.Cli.Api;

/// <summary>How the CLI reaches one node; the REST client, the auth client and the chat hub share it.</summary>
internal sealed class NodeConnectionOptions
{
    /// <summary>The node's loopback origin, as discovery returned it.</summary>
    public required Uri Origin { get; init; }

    /// <summary>Accept a self-signed server certificate (the dev host). Off by default; honoured for a loopback origin only.</summary>
    public bool AcceptAnyServerCertificate { get; init; }

    /// <summary>Test seam: returns a NEW transport handler per call in place of a socket handler.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }
}
