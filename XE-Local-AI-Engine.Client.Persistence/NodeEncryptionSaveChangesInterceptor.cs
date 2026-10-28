namespace XE_Local_AI_Engine.Client.Persistence;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

public sealed class NodeEncryptionSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<NodeEncryptedColumns.TrackedEncryptedProperty>> _pendingRestores = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        EncryptTrackedPayloads(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EncryptTrackedPayloads(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        RestoreTrackedPayloads(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreTrackedPayloads(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        RestoreTrackedPayloads(eventData.Context);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreTrackedPayloads(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Encrypts every <see cref="NodeEncryptedColumns" /> column on the tracked graph in place, remembering each original so
    ///     <see cref="RestoreTrackedPayloads" /> can put it back.
    /// </summary>
    private void EncryptTrackedPayloads(DbContext? context)
    {
        if (context is not NodeChatDbContext nodeContext)
        {
            return;
        }

        var trackedProperties = new List<NodeEncryptedColumns.TrackedEncryptedProperty>();

        foreach (var entity in NodeEncryptedColumns.Entities)
        {
            entity.EncryptTracked(nodeContext, trackedProperties);
        }

        if (trackedProperties.Count > 0)
        {
            _pendingRestores[nodeContext] = trackedProperties;
        }
    }

    private void RestoreTrackedPayloads(DbContext? context)
    {
        if (context is null || !_pendingRestores.TryRemove(context, out var trackedProperties))
        {
            return;
        }

        foreach (var trackedProperty in trackedProperties)
        {
            if (trackedProperty.PropertyEntry.EntityEntry.State == EntityState.Detached)
            {
                continue;
            }

            trackedProperty.PropertyEntry.CurrentValue = trackedProperty.Plaintext.ToArray();
            trackedProperty.PropertyEntry.OriginalValue = trackedProperty.Plaintext.ToArray();
            trackedProperty.PropertyEntry.IsModified = false;
        }
    }
}
