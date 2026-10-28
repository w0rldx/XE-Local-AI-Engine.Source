namespace XE_Local_AI_Engine.Client.Persistence;

using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Diagnostics;

public sealed class NodeEncryptionMaterializationInterceptor : IMaterializationInterceptor
{
    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (materializationData.Context is not NodeChatDbContext context)
        {
            return entity;
        }

        if (context.KeyProbe is { } probe)
        {
            // The node-key check's sampling pass: count the outcome instead of throwing.
            probe.Observe(entity, () => DecryptColumns(context, entity));
            return entity;
        }

        try
        {
            DecryptColumns(context, entity);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException && context.TryRecordUnreadableRow(entity))
        {
            // Only inside NodeChatDbContext.ListSkippingUnreadableAsync: that list drops this row, every other read still throws.
        }

        return entity;
    }

    private static void DecryptColumns(NodeChatDbContext context, object entity)
    {
        if (NodeEncryptedColumns.TryGet(entity.GetType(), out var encryptedEntity))
        {
            encryptedEntity.Decrypt(context, entity);
        }
    }
}
