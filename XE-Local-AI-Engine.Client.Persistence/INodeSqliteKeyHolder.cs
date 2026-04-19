namespace XE_Local_AI_Engine.Client.Persistence;

public interface INodeSqliteKeyHolder : IDisposable
{
    ReadOnlyMemory<byte> Key { get; }
}
