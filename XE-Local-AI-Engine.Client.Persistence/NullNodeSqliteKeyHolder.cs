namespace XE_Local_AI_Engine.Client.Persistence;

public sealed class NullNodeSqliteKeyHolder : INodeSqliteKeyHolder
{
    private static readonly byte[] ZeroKey = new byte[32];

    public ReadOnlyMemory<byte> Key => ZeroKey;

    public void Dispose()
    {
    }
}
