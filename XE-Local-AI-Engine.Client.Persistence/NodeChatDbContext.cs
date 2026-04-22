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

    internal ReadOnlyMemory<byte> NodeEncryptionKey => _nodeSqliteKeyHolder.Key;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureConversation(modelBuilder.Entity<NodeConversation>());
        ConfigureMessage(modelBuilder.Entity<NodeMessage>());
        ConfigureToolEvent(modelBuilder.Entity<NodeToolEvent>());
        ConfigurePurgedTombstone(modelBuilder.Entity<NodePurgedTombstone>());
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
}
