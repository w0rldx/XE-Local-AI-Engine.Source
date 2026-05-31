namespace XE_Local_AI_Engine.Client.Persistence;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeEncryptionSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentDictionary<DbContext, List<TrackedEncryptedProperty>> _pendingRestores = [];

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

    private void EncryptTrackedPayloads(DbContext? context)
    {
        if (context is not NodeChatDbContext nodeContext)
        {
            return;
        }

        var trackedProperties = new List<TrackedEncryptedProperty>();

        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeMessage>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Content), entry.Entity.ConversationId, entry.Entity.MessageId, "content", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.MetadataJson), entry.Entity.ConversationId, entry.Entity.MessageId, "metadata_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeToolEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.PlaintextArgs), entry.Entity.ConversationId, entry.Entity.ToolCallId, "plaintext_args", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.PlaintextResult), entry.Entity.ConversationId, entry.Entity.ToolCallId, "plaintext_result", trackedProperties);
        }

        // Selected folders are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // folder's own id plus the column name. See NodePayloadProtector for the associated-data layout.
        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeSelectedFolder>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.HostPath), Guid.Empty, entry.Entity.Id, "host_path", trackedProperties);
        }

        // Agent definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the definition's own id plus the column name — same layout as selected folders.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentDefinition>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Instructions), Guid.Empty, entry.Entity.Id, "instructions", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
        }

        // Playbook actions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // action's own id plus the column name — same layout as agent definitions. Behavior is required; the optional
        // trigger condition only encrypts when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<PlaybookAction>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Behavior), Guid.Empty, entry.Entity.Id, "behavior", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.TriggerCondition), Guid.Empty, entry.Entity.Id, "trigger_condition", trackedProperties);
        }

        // MCP server registrations are node-scoped, so the AAD binds the empty conversation id to the registration's
        // own id plus the column name. The secret-bearing columns (args, env, description) are all optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<McpServerRegistration>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ArgumentsJson), Guid.Empty, entry.Entity.Id, "arguments", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.EnvJson), Guid.Empty, entry.Entity.Id, "env", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
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

    private static void EncryptRequiredProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties)
        where TEntity : class
    {
        if (entry.State is not EntityState.Added and not EntityState.Modified)
        {
            return;
        }

        if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
        {
            return;
        }

        var plaintext = propertyEntry.CurrentValue;
        var context = (NodeChatDbContext)entry.Context;
        var plaintextCopy = plaintext.ToArray();

        propertyEntry.CurrentValue = NodePayloadProtector.Encrypt(plaintextCopy,
            context.NodeEncryptionKey.Span,
            conversationId,
            recordId,
            columnName);

        trackedProperties.Add(new TrackedEncryptedProperty(propertyEntry, plaintextCopy));
    }

    private static void EncryptOptionalProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]?> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties)
        where TEntity : class
    {
        if (entry.State is not EntityState.Added and not EntityState.Modified)
        {
            return;
        }

        if (entry.State == EntityState.Modified && !propertyEntry.IsModified)
        {
            return;
        }

        var plaintext = propertyEntry.CurrentValue;
        if (plaintext is null)
        {
            return;
        }

        var context = (NodeChatDbContext)entry.Context;
        var plaintextCopy = plaintext.ToArray();

        propertyEntry.CurrentValue = NodePayloadProtector.Encrypt(plaintextCopy,
            context.NodeEncryptionKey.Span,
            conversationId,
            recordId,
            columnName);

        trackedProperties.Add(new TrackedEncryptedProperty(propertyEntry, plaintextCopy));
    }

    private sealed record TrackedEncryptedProperty(PropertyEntry PropertyEntry, byte[] Plaintext);
}
