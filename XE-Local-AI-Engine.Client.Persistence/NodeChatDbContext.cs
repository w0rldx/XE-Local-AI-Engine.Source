namespace XE_Local_AI_Engine.Client.Persistence;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Configurations;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
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

    internal DbSet<CanvasWorkflow> CanvasWorkflows => Set<CanvasWorkflow>();

    internal DbSet<AgentSkill> AgentSkills => Set<AgentSkill>();

    internal DbSet<PlaybookAction> PlaybookActions => Set<PlaybookAction>();

    internal DbSet<AgentExecutionLog> AgentExecutionLogs => Set<AgentExecutionLog>();

    internal DbSet<GoldenConversation> GoldenConversations => Set<GoldenConversation>();

    internal DbSet<McpServerRegistration> McpServers => Set<McpServerRegistration>();

    internal DbSet<ModelClassification> ModelClassifications => Set<ModelClassification>();

    internal DbSet<ModelProviderMap> ModelProviderMaps => Set<ModelProviderMap>();

    internal DbSet<ScheduledJobDefinition> ScheduledJobDefinitions => Set<ScheduledJobDefinition>();

    internal DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();

    internal DbSet<ScheduledJobRunEvent> ScheduledJobRunEvents => Set<ScheduledJobRunEvent>();

    internal DbSet<ApprovedUtilityImage> ApprovedUtilityImages => Set<ApprovedUtilityImage>();

    internal DbSet<ModelFitSnapshot> ModelFitSnapshots => Set<ModelFitSnapshot>();

    internal DbSet<ModelFitRecommendation> ModelFitRecommendations => Set<ModelFitRecommendation>();

    internal DbSet<ModelFitBenchmark> ModelFitBenchmarks => Set<ModelFitBenchmark>();

    internal ReadOnlyMemory<byte> NodeEncryptionKey => _nodeSqliteKeyHolder.Key;

    /// <summary>
    ///     Encrypts a conversation title string for raw-SQL persistence. Returns null when the title is null so the
    ///     database column writes NULL. AAD mirrors the interceptor: conversationId appears as both conversation and
    ///     record id, column name is "title".
    /// </summary>
    public byte[]? EncryptConversationTitle(string? title, Guid conversationId)
    {
        if (title is null)
        {
            return null;
        }

        var plaintext = Encoding.UTF8.GetBytes(title);
        return NodePayloadProtector.Encrypt(plaintext, NodeEncryptionKey.Span, conversationId, conversationId, "title");
    }

    /// <summary>
    ///     Decrypts a raw title blob back to a string. Returns null when the blob is null. AAD mirrors the interceptor:
    ///     conversationId appears as both conversation and record id, column name is "title".
    /// </summary>
    public string? DecryptConversationTitle(byte[]? encrypted, Guid conversationId)
    {
        if (encrypted is null)
        {
            return null;
        }

        var plaintext = NodePayloadProtector.Decrypt(encrypted, NodeEncryptionKey.Span, conversationId, conversationId, "title");
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Decrypts a raw message content blob back to a string. Used by the title backfill service to re-derive titles
    ///     from the first user message after the EncryptConversationTitle migration. AAD = conversationId + messageId +
    ///     "content", matching the interceptor.
    /// </summary>
    public string DecryptMessageContent(byte[] encrypted, Guid conversationId, Guid messageId)
    {
        var plaintext = NodePayloadProtector.Decrypt(encrypted, NodeEncryptionKey.Span, conversationId, messageId, "content");
        return Encoding.UTF8.GetString(plaintext);
    }

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
        modelBuilder.ApplyConfiguration(new CanvasWorkflowConfiguration());
        modelBuilder.ApplyConfiguration(new AgentSkillConfiguration());
        modelBuilder.ApplyConfiguration(new PlaybookActionConfiguration());
        modelBuilder.ApplyConfiguration(new AgentExecutionLogConfiguration());
        modelBuilder.ApplyConfiguration(new GoldenConversationConfiguration());
        modelBuilder.ApplyConfiguration(new McpServerRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelClassificationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelProviderMapConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunEventConfiguration());
        modelBuilder.ApplyConfiguration(new ApprovedUtilityImageConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitRecommendationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitBenchmarkConfiguration());
    }
}
