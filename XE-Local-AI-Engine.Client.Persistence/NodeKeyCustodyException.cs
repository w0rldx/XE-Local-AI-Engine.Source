namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Startup refused because the node key cannot open this database: the key does not match it, or its key file is
///     missing. The host exits with a dedicated code instead of crashing.
/// </summary>
public sealed class NodeKeyCustodyException : InvalidOperationException
{
    public NodeKeyCustodyException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
