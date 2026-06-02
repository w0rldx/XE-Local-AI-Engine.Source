namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Represents node chat db context.
/// </summary>
public sealed class NodeChatDbContext : DbContext
{
    private readonly INodeSqliteKeyHolder _nodeSqliteKeyHolder;

    public NodeChatDbContext(DbContextOptions<NodeChatDbContext> options, INodeSqliteKeyHolder nodeSqliteKeyHolder) : base(options)
    {
        _nodeSqliteKeyHolder = nodeSqliteKeyHolder ?? throw new ArgumentNullException(nameof(nodeSqliteKeyHolder));
    }

    internal DbSet<NodeConversation> Conversations => Set<NodeConversation>();

    internal DbSet<NodeMessage> Messages => Set<NodeMessage>();

    internal DbSet<NodeToolEvent> ToolEvents => Set<NodeToolEvent>();

    internal DbSet<NodePurgedTombstone> PurgedTombstones => Set<NodePurgedTombstone>();

    internal DbSet<NodeMessageFeedback> MessageFeedback => Set<NodeMessageFeedback>();

    internal DbSet<NodeSelectedFolder> SelectedFolders => Set<NodeSelectedFolder>();

    internal DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

    internal DbSet<PlaybookAction> PlaybookActions => Set<PlaybookAction>();

    internal DbSet<GoldenConversation> GoldenConversations => Set<GoldenConversation>();

    internal DbSet<McpServerRegistration> McpServers => Set<McpServerRegistration>();

    internal DbSet<ModelClassification> ModelClassifications => Set<ModelClassification>();

    internal DbSet<ScheduledJobDefinition> ScheduledJobDefinitions => Set<ScheduledJobDefinition>();

    internal DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();

    internal DbSet<ScheduledJobRunEvent> ScheduledJobRunEvents => Set<ScheduledJobRunEvent>();

    internal ReadOnlyMemory<byte> NodeEncryptionKey => _nodeSqliteKeyHolder.Key;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureConversation(modelBuilder.Entity<NodeConversation>());
        ConfigureMessage(modelBuilder.Entity<NodeMessage>());
        ConfigureToolEvent(modelBuilder.Entity<NodeToolEvent>());
        ConfigurePurgedTombstone(modelBuilder.Entity<NodePurgedTombstone>());
        ConfigureMessageFeedback(modelBuilder.Entity<NodeMessageFeedback>());
        ConfigureSelectedFolder(modelBuilder.Entity<NodeSelectedFolder>());
        ConfigureAgentDefinition(modelBuilder.Entity<AgentDefinition>());
        ConfigurePlaybookAction(modelBuilder.Entity<PlaybookAction>());
        ConfigureGoldenConversation(modelBuilder.Entity<GoldenConversation>());
        ConfigureMcpServer(modelBuilder.Entity<McpServerRegistration>());
        ConfigureModelClassification(modelBuilder.Entity<ModelClassification>());
        ConfigureScheduledJobDefinition(modelBuilder.Entity<ScheduledJobDefinition>());
        ConfigureScheduledJobRun(modelBuilder.Entity<ScheduledJobRun>());
        ConfigureScheduledJobRunEvent(modelBuilder.Entity<ScheduledJobRunEvent>());
    }

    private static void ConfigureConversation(EntityTypeBuilder<NodeConversation> builder)
    {
        builder.ToTable("conversations");
        builder.HasKey(entity => entity.ConversationId);

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Title)
               .HasColumnName("title");

        builder.Property(entity => entity.UserId)
               .HasColumnName("user_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.LastSeenUtc)
               .HasColumnName("last_seen_utc");

        builder.Property(entity => entity.Purged)
               .HasColumnName("purged");

        builder.Property(entity => entity.IsPinned)
               .HasColumnName("is_pinned")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Archived)
               .HasColumnName("archived")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Origin)
               .HasColumnName("origin")
               .HasDefaultValue(NodeChatOrigin.Local);

        builder.Property(entity => entity.BranchOfConversationId)
               .HasColumnName("branch_of_conversation_id");

        builder.Property(entity => entity.SelectedPathJson)
               .HasColumnName("selected_path_json");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.HasMany(entity => entity.Messages)
               .WithOne(entity => entity.Conversation)
               .HasForeignKey(entity => entity.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(entity => entity.ToolEvents)
               .WithOne(entity => entity.Conversation)
               .HasForeignKey(entity => entity.ConversationId)
               .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMessage(EntityTypeBuilder<NodeMessage> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(entity => entity.MessageId);

        builder.Property(entity => entity.MessageId)
               .HasColumnName("message_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Sequence)
               .HasColumnName("sequence");

        builder.Property(entity => entity.Role)
               .HasColumnName("role");

        builder.Property(entity => entity.Content)
               .HasColumnName("content");

        builder.Property(entity => entity.MetadataJson)
               .HasColumnName("metadata_json");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.Property(entity => entity.Status)
               .HasColumnName("status")
               .HasDefaultValue(NodeMessageStatus.Completed);

        builder.Property(entity => entity.Origin)
               .HasColumnName("origin")
               .HasDefaultValue(NodeChatOrigin.Local);

        builder.Property(entity => entity.RequestId)
               .HasColumnName("request_id");

        builder.Property(entity => entity.Error)
               .HasColumnName("error");

        builder.Property(entity => entity.ParentMessageId)
               .HasColumnName("parent_message_id");

        builder.Property(entity => entity.VariantGroupId)
               .HasColumnName("variant_group_id");

        builder.HasIndex(entity => entity.RequestId);
        builder.HasIndex(entity => entity.ParentMessageId);
        builder.HasIndex(entity => entity.VariantGroupId);
    }

    private static void ConfigureToolEvent(EntityTypeBuilder<NodeToolEvent> builder)
    {
        builder.ToTable("tool_events");
        builder.HasKey(entity => entity.ToolCallId);

        builder.Property(entity => entity.ToolCallId)
               .HasColumnName("tool_call_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.ToolName)
               .HasColumnName("tool_name");

        builder.Property(entity => entity.PlaintextArgs)
               .HasColumnName("plaintext_args");

        builder.Property(entity => entity.PlaintextResult)
               .HasColumnName("plaintext_result");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");
    }

    private static void ConfigurePurgedTombstone(EntityTypeBuilder<NodePurgedTombstone> builder)
    {
        builder.ToTable("purged_tombstones");
        builder.HasKey(entity => entity.ConversationId);

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.PurgedAtUtc)
               .HasColumnName("purged_at_utc");

        builder.Property(entity => entity.AckedAtUtc)
               .HasColumnName("acked_at_utc");
    }

    private static void ConfigureMessageFeedback(EntityTypeBuilder<NodeMessageFeedback> builder)
    {
        builder.ToTable("message_feedback");
        builder.HasKey(entity => entity.MessageId);

        builder.Property(entity => entity.MessageId)
               .HasColumnName("message_id");

        builder.Property(entity => entity.ConversationId)
               .HasColumnName("conversation_id");

        builder.Property(entity => entity.Rating)
               .HasColumnName("rating");

        builder.Property(entity => entity.Comment)
               .HasColumnName("comment");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.HasOne(entity => entity.Message)
               .WithOne()
               .HasForeignKey<NodeMessageFeedback>(entity => entity.MessageId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(entity => entity.ConversationId);
    }

    private static void ConfigureSelectedFolder(EntityTypeBuilder<NodeSelectedFolder> builder)
    {
        builder.ToTable("selected_folders");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Alias)
               .HasColumnName("alias");

        builder.Property(entity => entity.HostPath)
               .HasColumnName("host_path");

        builder.Property(entity => entity.Mode)
               .HasColumnName("mode")
               .HasDefaultValue(SelectedFolderMode.Copy);

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.HasIndex(entity => entity.Alias)
               .IsUnique();
    }

    private static void ConfigureAgentDefinition(EntityTypeBuilder<AgentDefinition> builder)
    {
        builder.ToTable("agent_definitions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Instructions)
               .HasColumnName("instructions");

        builder.Property(entity => entity.ModelProfile)
               .HasColumnName("model_profile");

        builder.Property(entity => entity.ReasoningEffort)
               .HasColumnName("reasoning_effort");

        builder.Property(entity => entity.Kind)
               .HasColumnName("kind")
               .HasDefaultValue((int)AgentDefinitionKind.Single);

        builder.Property(entity => entity.AllowedToolNamesJson)
               .HasColumnName("allowed_tool_names_json");

        builder.Property(entity => entity.ToolApprovalsJson)
               .HasColumnName("tool_approvals_json");

        builder.Property(entity => entity.OrchestrationTopologyJson)
               .HasColumnName("orchestration_topology_json");

        builder.Property(entity => entity.PlaybookEnabled)
               .HasColumnName("playbook_enabled")
               .HasDefaultValue(false);

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name);
    }

    private static void ConfigurePlaybookAction(EntityTypeBuilder<PlaybookAction> builder)
    {
        builder.ToTable("playbook_actions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.Property(entity => entity.State)
               .HasColumnName("state");

        builder.Property(entity => entity.Source)
               .HasColumnName("source");

        builder.Property(entity => entity.TriggerCondition)
               .HasColumnName("trigger_condition");

        builder.Property(entity => entity.Behavior)
               .HasColumnName("behavior");

        builder.Property(entity => entity.Scope)
               .HasColumnName("scope");

        // P3 analysis provenance/confidence — additive nullable columns. Plaintext (ids only / a scalar), not encrypted.
        builder.Property(entity => entity.SourceFeedbackIds)
               .HasColumnName("source_feedback_ids");

        builder.Property(entity => entity.Confidence)
               .HasColumnName("confidence");

        // P4 eval-gate outcome — additive nullable column. Plaintext (ids + flags + counts only), not encrypted.
        builder.Property(entity => entity.EvalResult)
               .HasColumnName("eval_result");

        builder.Property(entity => entity.Priority)
               .HasColumnName("priority");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // P5 cohort-monitoring clock — additive nullable column. Plaintext (a timestamp), not encrypted.
        builder.Property(entity => entity.EnabledAtUtc)
               .HasColumnName("enabled_at_utc");

        builder.HasIndex(entity => entity.AgentDefinitionId);

        // A playbook action is meaningless without its owning agent, so the FK cascades: deleting an agent removes its
        // actions. (Contrast conversation->definition, which is intentionally no-FK because a conversation outlives its
        // definition.)
        builder.HasOne<AgentDefinition>()
               .WithMany()
               .HasForeignKey(entity => entity.AgentDefinitionId)
               .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureGoldenConversation(EntityTypeBuilder<GoldenConversation> builder)
    {
        builder.ToTable("golden_conversations");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.AgentDefinitionId)
               .HasColumnName("agent_definition_id");

        builder.Property(entity => entity.Title)
               .HasColumnName("title");

        builder.Property(entity => entity.InputTurns)
               .HasColumnName("input_turns");

        builder.Property(entity => entity.Assertion)
               .HasColumnName("assertion");

        builder.Property(entity => entity.Rubric)
               .HasColumnName("rubric");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled");

        // Harvest provenance — additive columns. Plaintext (an enum + two ids), not encrypted; the sensitive harvested
        // text reuses the already-encrypted input_turns/rubric columns.
        builder.Property(entity => entity.Source)
               .HasColumnName("source")
               .HasDefaultValue(GoldenConversationSource.Manual);

        builder.Property(entity => entity.SourceMessageId)
               .HasColumnName("source_message_id");

        builder.Property(entity => entity.SourceConversationId)
               .HasColumnName("source_conversation_id");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.HasIndex(entity => entity.AgentDefinitionId);

        // A golden conversation belongs to its agent's evaluation set, so the FK cascades: deleting an agent removes its
        // golden cases — same layout as playbook_actions.
        builder.HasOne<AgentDefinition>()
               .WithMany()
               .HasForeignKey(entity => entity.AgentDefinitionId)
               .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureMcpServer(EntityTypeBuilder<McpServerRegistration> builder)
    {
        builder.ToTable("mcp_servers");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.Name)
               .HasColumnName("name")
               // Case-insensitive collation so the unique index treats names differing only in case as duplicates,
               // matching the application-layer service's case-insensitive name handling.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.TransportKind)
               .HasColumnName("transport_kind");

        builder.Property(entity => entity.Command)
               .HasColumnName("command");

        builder.Property(entity => entity.ArgumentsJson)
               .HasColumnName("arguments");

        builder.Property(entity => entity.WorkingDirectory)
               .HasColumnName("working_directory");

        builder.Property(entity => entity.EnvJson)
               .HasColumnName("env");

        builder.Property(entity => entity.Url)
               .HasColumnName("url");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled");

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // The server Name is the source of the qualified tool-name slug, so uniqueness keeps tool names collision-free.
        builder.HasIndex(entity => entity.Name)
               .IsUnique();
    }

    private static void ConfigureModelClassification(EntityTypeBuilder<ModelClassification> builder)
    {
        builder.ToTable("model_classifications");
        builder.HasKey(entity => entity.ModelName);

        builder.Property(entity => entity.ModelName)
               .HasColumnName("model_name")
               // Case-insensitive collation so the model-name primary key and lookups treat names differing only in
               // case as the same model, matching the application-layer service's case-insensitive handling.
               .UseCollation("NOCASE");

        builder.Property(entity => entity.Digest)
               .HasColumnName("digest");

        builder.Property(entity => entity.DetectedKind)
               .HasColumnName("detected_kind")
               .HasDefaultValue(ModelKind.Unknown);

        builder.Property(entity => entity.DetectedCapabilitiesJson)
               .HasColumnName("detected_capabilities_json");

        builder.Property(entity => entity.OverrideKind)
               .HasColumnName("override_kind");

        builder.Property(entity => entity.DetectedAtUtc)
               .HasColumnName("detected_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");
    }

    private static void ConfigureScheduledJobDefinition(EntityTypeBuilder<ScheduledJobDefinition> builder)
    {
        builder.ToTable("scheduled_job_definitions");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.TemplateId)
               .HasColumnName("template_id");

        builder.Property(entity => entity.DisplayName)
               .HasColumnName("display_name");

        builder.Property(entity => entity.Description)
               .HasColumnName("description");

        builder.Property(entity => entity.Enabled)
               .HasColumnName("enabled")
               .HasDefaultValue(true);

        builder.Property(entity => entity.ScheduleKind)
               .HasColumnName("schedule_kind");

        builder.Property(entity => entity.CronExpression)
               .HasColumnName("cron_expression");

        builder.Property(entity => entity.IntervalSeconds)
               .HasColumnName("interval_seconds");

        builder.Property(entity => entity.RepeatCount)
               .HasColumnName("repeat_count");

        builder.Property(entity => entity.StartAtUtc)
               .HasColumnName("start_at_utc");

        builder.Property(entity => entity.EndAtUtc)
               .HasColumnName("end_at_utc");

        builder.Property(entity => entity.TimeZoneId)
               .HasColumnName("time_zone_id")
               .HasDefaultValue("UTC");

        builder.Property(entity => entity.MisfirePolicy)
               .HasColumnName("misfire_policy");

        builder.Property(entity => entity.PreventOverlap)
               .HasColumnName("prevent_overlap")
               .HasDefaultValue(false);

        builder.Property(entity => entity.MaxRuntimeSeconds)
               .HasColumnName("max_runtime_seconds");

        builder.Property(entity => entity.ParameterJson)
               .HasColumnName("parameter_json");

        builder.Property(entity => entity.CreatedBy)
               .HasColumnName("created_by");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        builder.Property(entity => entity.DisabledAtUtc)
               .HasColumnName("disabled_at_utc");

        builder.Property(entity => entity.DeletedAtUtc)
               .HasColumnName("deleted_at_utc");

        builder.HasIndex(entity => new { entity.TemplateId, entity.Enabled });
    }

    private static void ConfigureScheduledJobRun(EntityTypeBuilder<ScheduledJobRun> builder)
    {
        builder.ToTable("scheduled_job_runs");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.ScheduledJobId)
               .HasColumnName("scheduled_job_id");

        builder.Property(entity => entity.TemplateId)
               .HasColumnName("template_id");

        builder.Property(entity => entity.QuartzFireInstanceId)
               .HasColumnName("quartz_fire_instance_id");

        builder.Property(entity => entity.TriggeredBy)
               .HasColumnName("triggered_by");

        builder.Property(entity => entity.Status)
               .HasColumnName("status");

        builder.Property(entity => entity.ScheduledFireTimeUtc)
               .HasColumnName("scheduled_fire_time_utc");

        builder.Property(entity => entity.ActualFireTimeUtc)
               .HasColumnName("actual_fire_time_utc");

        builder.Property(entity => entity.CompletedAtUtc)
               .HasColumnName("completed_at_utc");

        builder.Property(entity => entity.DurationMs)
               .HasColumnName("duration_ms");

        builder.Property(entity => entity.Summary)
               .HasColumnName("summary");

        builder.Property(entity => entity.DetailsJson)
               .HasColumnName("details_json");

        builder.Property(entity => entity.ErrorMessage)
               .HasColumnName("error_message");

        builder.Property(entity => entity.ErrorDetails)
               .HasColumnName("error_details");

        builder.Property(entity => entity.CancellationRequestedAtUtc)
               .HasColumnName("cancellation_requested_at_utc");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.HasIndex(entity => new { entity.ScheduledJobId, entity.ActualFireTimeUtc });

        // The fire-instance id is the idempotency key for the upsert, so it is unique — but only among rows that
        // actually carry one (manual/system runs leave it null), hence the filtered unique index.
        builder.HasIndex(entity => entity.QuartzFireInstanceId)
               .IsUnique()
               .HasFilter("quartz_fire_instance_id IS NOT NULL");

        // A run intentionally has NO enforced FK to its definition: runs outlive definitions (a removed/soft-deleted
        // definition must not cascade away its run history). Same intentional no-FK precedent as conversation->definition.
    }

    private static void ConfigureScheduledJobRunEvent(EntityTypeBuilder<ScheduledJobRunEvent> builder)
    {
        builder.ToTable("scheduled_job_run_events");
        builder.HasKey(entity => entity.Id);

        builder.Property(entity => entity.Id)
               .HasColumnName("id");

        builder.Property(entity => entity.RunId)
               .HasColumnName("run_id");

        builder.Property(entity => entity.Sequence)
               .HasColumnName("sequence");

        builder.Property(entity => entity.Level)
               .HasColumnName("level");

        builder.Property(entity => entity.Message)
               .HasColumnName("message");

        builder.Property(entity => entity.DataJson)
               .HasColumnName("data_json");

        builder.Property(entity => entity.OccurredAtUtc)
               .HasColumnName("occurred_at_utc");

        builder.HasIndex(entity => new { entity.RunId, entity.Sequence })
               .IsUnique();

        // An event is meaningless without its owning run, so the FK cascades: deleting a run removes its events.
        builder.HasOne<ScheduledJobRun>()
               .WithMany()
               .HasForeignKey(entity => entity.RunId)
               .OnDelete(DeleteBehavior.Cascade);
    }
}
