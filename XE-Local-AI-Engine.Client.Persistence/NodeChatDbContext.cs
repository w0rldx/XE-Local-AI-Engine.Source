namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using XE_Local_AI_Engine.Client.Persistence.Entities;

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

        builder.Property(entity => entity.Version)
               .HasColumnName("version");

        builder.Property(entity => entity.CreatedAtUtc)
               .HasColumnName("created_at_utc");

        builder.Property(entity => entity.UpdatedAtUtc)
               .HasColumnName("updated_at_utc");

        // Name is a human label, not a key: index it for list/search but do not enforce uniqueness.
        builder.HasIndex(entity => entity.Name);
    }
}
