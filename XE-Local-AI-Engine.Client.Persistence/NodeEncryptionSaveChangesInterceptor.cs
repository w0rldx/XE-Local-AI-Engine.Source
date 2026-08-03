namespace XE_Local_AI_Engine.Client.Persistence;

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Represents node encryption save changes interceptor.
/// </summary>
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

        // Conversation titles are conversation-scoped: AAD binds the conversation's own id as both the
        // conversation id and the record id, plus the column name. Title is optional (null until the first
        // user message is received).
        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeConversation>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Title), entry.Entity.ConversationId, entry.Entity.ConversationId, "title", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<NodeMessage>())
        {
            // Content and metadata are the only encrypted columns with legacy plaintext rows on disk, so they carry the
            // versioned read-both envelope (NodeChatContentProtection) instead of the bare protector. This keeps an
            // EF-tracked save byte-compatible with the raw-ADO persistence path.
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Content), entry.Entity.ConversationId, entry.Entity.MessageId, "content", trackedProperties,
                NodeChatContentProtection.Protect);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.MetadataJson), entry.Entity.ConversationId, entry.Entity.MessageId, "metadata_json", trackedProperties,
                NodeChatContentProtection.Protect);
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

        // Development templates and the provenance of repositories materialized from them both carry host paths, so
        // they get exactly the selected-folder treatment: node-scoped AAD binding the empty conversation id to the
        // row's own id plus the column name.
        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTemplate>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.HostPath), Guid.Empty, entry.Entity.Id, "host_path", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTemplateMaterialization>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.TemplatePath), Guid.Empty, entry.Entity.SelectedFolderId, "template_path", trackedProperties);
        }

        // Agent definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the definition's own id plus the column name — same layout as selected folders.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentDefinition>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Instructions), Guid.Empty, entry.Entity.Id, "instructions", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
        }

        // Canvas workflows are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // workflow's own id plus the column name — same layout as agent definitions. The serialized graph (which carries
        // agent instructions and Start text) is a required encrypted column.
        foreach (var entry in nodeContext.ChangeTracker.Entries<CanvasWorkflow>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.GraphJson), Guid.Empty, entry.Entity.Id, "graph_json", trackedProperties);
        }

        // Agent skills are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // skill's own id plus the column name — same layout as agent definitions. Both the description and the SKILL.md
        // body are required encrypted columns.
        foreach (var entry in nodeContext.ChangeTracker.Entries<AgentSkill>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Body), Guid.Empty, entry.Entity.Id, "body", trackedProperties);
        }

        // Playbook actions are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the
        // action's own id plus the column name — same layout as agent definitions. Behavior is required; the optional
        // trigger condition only encrypts when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<PlaybookAction>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Behavior), Guid.Empty, entry.Entity.Id, "behavior", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.TriggerCondition), Guid.Empty, entry.Entity.Id, "trigger_condition", trackedProperties);
        }

        // Golden conversations are node-scoped (no conversation/message), so the AAD binds the empty conversation id to
        // the case's own id plus the column name — same layout as playbook actions. InputTurns is required; the optional
        // assertion/rubric only encrypt when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<GoldenConversation>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.InputTurns), Guid.Empty, entry.Entity.Id, "input_turns", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Assertion), Guid.Empty, entry.Entity.Id, "assertion", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Rubric), Guid.Empty, entry.Entity.Id, "rubric", trackedProperties);
        }

        // MCP server registrations are node-scoped, so the AAD binds the empty conversation id to the registration's
        // own id plus the column name. The secret-bearing columns (args, env, description) are all optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<McpServerRegistration>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ArgumentsJson), Guid.Empty, entry.Entity.Id, "arguments", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.EnvJson), Guid.Empty, entry.Entity.Id, "env", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.Description), Guid.Empty, entry.Entity.Id, "description", trackedProperties);
        }

        // The inbound-MCP bearer credential is node-scoped, so the AAD binds the empty conversation id to the row's own
        // (constant, singleton) id plus the column name. The key material is required — a row without it would
        // authenticate nothing — so it always encrypts.
        foreach (var entry in nodeContext.ChangeTracker.Entries<McpServerApiKey>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Material), Guid.Empty, entry.Entity.Id, "mcp_api_key_material", trackedProperties);
        }

        // Scheduled job definitions are node-scoped (no conversation/message), so the AAD binds the empty conversation
        // id to the definition's own id plus the column name. Only the opaque job parameters are encrypted; they are
        // optional, so they encrypt only when present.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobDefinition>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ParameterJson), Guid.Empty, entry.Entity.Id, "parameter_json", trackedProperties);
        }

        // Scheduled job runs are node-scoped, so the AAD binds the empty conversation id to the run's own id plus the
        // column name. Only the structured run detail is encrypted; it is optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobRun>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailsJson), Guid.Empty, entry.Entity.Id, "details_json", trackedProperties);
        }

        // Scheduled job run events are node-scoped, so the AAD binds the empty conversation id to the event's own id
        // plus the column name. Only the structured event payload is encrypted; it is optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ScheduledJobRunEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DataJson), Guid.Empty, entry.Entity.Id, "data_json", trackedProperties);
        }

        // Model-fit snapshots are node-scoped, so the AAD binds the empty conversation id to the snapshot's own id plus
        // the column name. The raw utility output, stderr excerpt and detailed diagnostics are sensitive and optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ModelFitSnapshot>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.RawJson), Guid.Empty, entry.Entity.Id, "raw_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.StderrExcerpt), Guid.Empty, entry.Entity.Id, "stderr_excerpt", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DiagnosticsJson), Guid.Empty, entry.Entity.Id, "diagnostics_json", trackedProperties);
        }

        // Model-fit benchmark rows are node-scoped, so the AAD binds the empty conversation id to the row's own id plus
        // the column name. Distinct AAD column names (bench_*) avoid cross-entity collision with the snapshot columns.
        // The raw benchmark output and diagnostics are sensitive and optional.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ModelFitBenchmark>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.RawJson), Guid.Empty, entry.Entity.Id, "bench_raw_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DiagnosticsJson), Guid.Empty, entry.Entity.Id, "bench_diagnostics_json", trackedProperties);
        }

        // Uploaded files are conversation-scoped: the AAD binds the owning conversation id to the file's own id plus the
        // column name — same layout as conversation titles. Only the display name is encrypted (the durable bytes and
        // extracted text are encrypted on disk by the file store, not in a column). The store's raw-SQL write path uses
        // NodeChatDbContext.EncryptUploadedFileName with the identical protector/AAD, so an EF save (used by tests) and
        // the raw-SQL write are interchangeable; this guard keeps the column encrypted for any EF-tracked save.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ConversationUploadedFile>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.OriginalFileName), entry.Entity.ConversationId, entry.Entity.FileId, "original_file_name", trackedProperties);
        }

        // Image jobs are node-scoped (no conversation/message), so the AAD binds the empty conversation id to the job's
        // own id plus the column name — same layout as agent definitions. The prompt is required; the negative prompt
        // only encrypts when present. Distinct AAD column names (image_*) avoid cross-entity collision.
        foreach (var entry in nodeContext.ChangeTracker.Entries<ImageJob>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Prompt), Guid.Empty, entry.Entity.Id, "image_prompt", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.NegativePrompt), Guid.Empty, entry.Entity.Id, "image_negative_prompt", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentProject>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Objective), entry.Entity.Id, entry.Entity.Id, "development_objective", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentTask>())
        {
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Title), entry.Entity.ProjectId, entry.Entity.Id, "development_task_title", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.Requirements), entry.Entity.ProjectId, entry.Entity.Id, "development_task_requirements", trackedProperties);
            EncryptRequiredProperty(entry, entry.Property(entity => entity.AcceptanceCriteriaJson), entry.Entity.ProjectId, entry.Entity.Id, "development_acceptance_criteria_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentArtifact>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ContentJson), entry.Entity.ProjectId, entry.Entity.Id, "development_artifact_content_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.InputArtifactIdsJson), entry.Entity.ProjectId, entry.Entity.Id, "development_artifact_input_ids_json", trackedProperties);
        }

        foreach (var entry in nodeContext.ChangeTracker.Entries<DevelopmentEvent>())
        {
            EncryptOptionalProperty(entry, entry.Property(entity => entity.DetailJson), entry.Entity.ProjectId, entry.Entity.Id, "development_event_detail_json", trackedProperties);
            EncryptOptionalProperty(entry, entry.Property(entity => entity.ResultMetadataJson), entry.Entity.ProjectId, entry.Entity.Id, "development_event_result_json", trackedProperties);
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

    // The at-rest transform for one column. Both the bare protector (NodePayloadProtector.Encrypt, the default for
    // every column that shipped encrypted from creation) and the versioned read-both envelope
    // (NodeChatContentProtection.Protect, used for message content/metadata) match this shape.
    private delegate byte[] PayloadProtector(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, Guid conversationId, Guid recordId, string columnName);

    private static void EncryptRequiredProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties)
        where TEntity : class
    {
        EncryptRequiredProperty(entry, propertyEntry, conversationId, recordId, columnName, trackedProperties, NodePayloadProtector.Encrypt);
    }

    private static void EncryptRequiredProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties,
        PayloadProtector protect)
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

        propertyEntry.CurrentValue = protect(plaintextCopy,
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
        EncryptOptionalProperty(entry, propertyEntry, conversationId, recordId, columnName, trackedProperties, NodePayloadProtector.Encrypt);
    }

    private static void EncryptOptionalProperty<TEntity>(EntityEntry<TEntity> entry,
        PropertyEntry<TEntity, byte[]?> propertyEntry,
        Guid conversationId,
        Guid recordId,
        string columnName,
        ICollection<TrackedEncryptedProperty> trackedProperties,
        PayloadProtector protect)
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

        propertyEntry.CurrentValue = protect(plaintextCopy,
            context.NodeEncryptionKey.Span,
            conversationId,
            recordId,
            columnName);

        trackedProperties.Add(new TrackedEncryptedProperty(propertyEntry, plaintextCopy));
    }

    private sealed record TrackedEncryptedProperty(PropertyEntry PropertyEntry, byte[] Plaintext);
}
