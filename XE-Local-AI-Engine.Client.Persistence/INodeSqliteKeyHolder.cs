namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Abstraction for node sqlite key holder behavior.
/// </summary>
public interface INodeSqliteKeyHolder : IDisposable
{
    ReadOnlyMemory<byte> Key { get; }
}
