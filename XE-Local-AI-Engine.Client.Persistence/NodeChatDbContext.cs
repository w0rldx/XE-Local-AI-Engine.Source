namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Configurations;
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

    internal DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();

    internal DbSet<PlaybookAction> PlaybookActions => Set<PlaybookAction>();

    internal DbSet<GoldenConversation> GoldenConversations => Set<GoldenConversation>();

    internal DbSet<McpServerRegistration> McpServers => Set<McpServerRegistration>();

    internal DbSet<ModelClassification> ModelClassifications => Set<ModelClassification>();

    internal DbSet<ScheduledJobDefinition> ScheduledJobDefinitions => Set<ScheduledJobDefinition>();

    internal DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();

    internal DbSet<ScheduledJobRunEvent> ScheduledJobRunEvents => Set<ScheduledJobRunEvent>();

    internal DbSet<ApprovedUtilityImage> ApprovedUtilityImages => Set<ApprovedUtilityImage>();

    internal DbSet<ModelFitSnapshot> ModelFitSnapshots => Set<ModelFitSnapshot>();

    internal DbSet<ModelFitRecommendation> ModelFitRecommendations => Set<ModelFitRecommendation>();

    internal DbSet<ModelFitBenchmark> ModelFitBenchmarks => Set<ModelFitBenchmark>();

    internal ReadOnlyMemory<byte> NodeEncryptionKey => _nodeSqliteKeyHolder.Key;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfiguration(new NodeConversationConfiguration());
        modelBuilder.ApplyConfiguration(new NodeMessageConfiguration());
        modelBuilder.ApplyConfiguration(new NodeToolEventConfiguration());
        modelBuilder.ApplyConfiguration(new NodePurgedTombstoneConfiguration());
        modelBuilder.ApplyConfiguration(new NodeMessageFeedbackConfiguration());
        modelBuilder.ApplyConfiguration(new NodeSelectedFolderConfiguration());
        modelBuilder.ApplyConfiguration(new AgentDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new AgentSkillConfiguration());
        modelBuilder.ApplyConfiguration(new PlaybookActionConfiguration());
        modelBuilder.ApplyConfiguration(new GoldenConversationConfiguration());
        modelBuilder.ApplyConfiguration(new McpServerRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelClassificationConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunEventConfiguration());
        modelBuilder.ApplyConfiguration(new ApprovedUtilityImageConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitRecommendationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitBenchmarkConfiguration());
    }
}
