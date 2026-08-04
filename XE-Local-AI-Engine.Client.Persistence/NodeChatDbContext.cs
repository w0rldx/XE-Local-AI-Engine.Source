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

    internal DbSet<AgentSkillResource> AgentSkillResources => Set<AgentSkillResource>();

    internal DbSet<PlaybookAction> PlaybookActions => Set<PlaybookAction>();

    internal DbSet<AgentExecutionLog> AgentExecutionLogs => Set<AgentExecutionLog>();

    internal DbSet<GoldenConversation> GoldenConversations => Set<GoldenConversation>();

    internal DbSet<McpServerRegistration> McpServers => Set<McpServerRegistration>();

    /// <summary>The singleton inbound-MCP bearer credential. See <see cref="McpServerApiKey" /> for the direction split.</summary>
    internal DbSet<McpServerApiKey> McpServerApiKeys => Set<McpServerApiKey>();

    internal DbSet<ModelClassification> ModelClassifications => Set<ModelClassification>();

    internal DbSet<ModelProviderMap> ModelProviderMaps => Set<ModelProviderMap>();

    internal DbSet<ScheduledJobDefinition> ScheduledJobDefinitions => Set<ScheduledJobDefinition>();

    internal DbSet<ScheduledJobRun> ScheduledJobRuns => Set<ScheduledJobRun>();

    internal DbSet<ScheduledJobRunEvent> ScheduledJobRunEvents => Set<ScheduledJobRunEvent>();

    internal DbSet<ModelFitSnapshot> ModelFitSnapshots => Set<ModelFitSnapshot>();

    internal DbSet<ModelFitRecommendation> ModelFitRecommendations => Set<ModelFitRecommendation>();

    internal DbSet<ModelFitBenchmark> ModelFitBenchmarks => Set<ModelFitBenchmark>();

    internal DbSet<InferenceProfile> InferenceProfiles => Set<InferenceProfile>();

    internal DbSet<ConversationUploadedFile> UploadedFiles => Set<ConversationUploadedFile>();

    internal DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    internal DbSet<KnowledgeDocumentSection> KnowledgeDocumentSections => Set<KnowledgeDocumentSection>();

    internal DbSet<KnowledgeDocumentChunk> KnowledgeDocumentChunks => Set<KnowledgeDocumentChunk>();

    internal DbSet<KnowledgeChunkVector> KnowledgeChunkVectors => Set<KnowledgeChunkVector>();

    internal DbSet<ImageJob> ImageJobs => Set<ImageJob>();

    internal DbSet<GeneratedImage> GeneratedImages => Set<GeneratedImage>();

    internal DbSet<ImageModelProfile> ImageModelProfiles => Set<ImageModelProfile>();

    internal DbSet<ChatMaintenanceState> MaintenanceState => Set<ChatMaintenanceState>();

    internal DbSet<DevelopmentProject> DevelopmentProjects => Set<DevelopmentProject>();

    internal DbSet<DevelopmentTask> DevelopmentTasks => Set<DevelopmentTask>();

    internal DbSet<DevelopmentAttempt> DevelopmentAttempts => Set<DevelopmentAttempt>();

    internal DbSet<DevelopmentArtifact> DevelopmentArtifacts => Set<DevelopmentArtifact>();

    internal DbSet<DevelopmentEvent> DevelopmentEvents => Set<DevelopmentEvent>();

    internal DbSet<DevelopmentTemplate> DevelopmentTemplates => Set<DevelopmentTemplate>();

    internal DbSet<DevelopmentTemplateMaterialization> DevelopmentTemplateMaterializations => Set<DevelopmentTemplateMaterialization>();

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
    ///     Encrypts a message content string into the versioned at-rest envelope for the raw-ADO persistence path. AAD =
    ///     conversationId + messageId + "content", matching the interceptors and <see cref="DecryptMessageContent" />.
    /// </summary>
    public byte[] EncryptMessageContent(string content, Guid conversationId, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(content);

        return NodeChatContentProtection.Protect(Encoding.UTF8.GetBytes(content), NodeEncryptionKey.Span, conversationId, messageId, "content");
    }

    /// <summary>
    ///     Decrypts a raw message content blob back to a string. Read-both: an enveloped blob is decrypted; a legacy
    ///     plaintext blob (written before content encryption shipped) is returned verbatim. AAD = conversationId +
    ///     messageId + "content", matching the interceptors.
    /// </summary>
    public string DecryptMessageContent(byte[] stored, Guid conversationId, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(stored);

        var plaintext = NodeChatContentProtection.Unprotect(stored, NodeEncryptionKey.Span, conversationId, messageId, "content");
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Encrypts a serialized <c>metadata_json</c> UTF-8 blob into the versioned at-rest envelope for the raw-ADO
    ///     persistence path. Returns null when the blob is null so the column writes NULL. AAD = conversationId +
    ///     messageId + "metadata_json".
    /// </summary>
    public byte[]? EncryptMessageMetadata(byte[]? metadataJsonUtf8, Guid conversationId, Guid messageId)
    {
        if (metadataJsonUtf8 is null)
        {
            return null;
        }

        return NodeChatContentProtection.Protect(metadataJsonUtf8, NodeEncryptionKey.Span, conversationId, messageId, "metadata_json");
    }

    /// <summary>
    ///     Decrypts a raw <c>metadata_json</c> blob back to its UTF-8 JSON string. Read-both, mirroring
    ///     <see cref="DecryptMessageContent" />; returns null when the blob is null.
    /// </summary>
    public string? DecryptMessageMetadata(byte[]? stored, Guid conversationId, Guid messageId)
    {
        if (stored is null)
        {
            return null;
        }

        var plaintext = NodeChatContentProtection.Unprotect(stored, NodeEncryptionKey.Span, conversationId, messageId, "metadata_json");
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Idempotently upgrades a stored message content blob to the encrypted envelope: an already-enveloped blob is
    ///     returned unchanged, and a legacy plaintext blob (the bytes themselves are the plaintext) is encrypted. Used by
    ///     the content-encryption migration so a re-run never re-encrypts an already-migrated row.
    /// </summary>
    public byte[] EnsureMessageContentEncrypted(byte[] stored, Guid conversationId, Guid messageId)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return NodeChatContentProtection.IsProtected(stored)
            ? stored
            : NodeChatContentProtection.Protect(stored, NodeEncryptionKey.Span, conversationId, messageId, "content");
    }

    /// <summary>
    ///     Idempotently upgrades a stored <c>metadata_json</c> blob to the encrypted envelope, mirroring
    ///     <see cref="EnsureMessageContentEncrypted" />. Null (no metadata) stays null.
    /// </summary>
    public byte[]? EnsureMessageMetadataEncrypted(byte[]? stored, Guid conversationId, Guid messageId)
    {
        if (stored is null)
        {
            return null;
        }

        return NodeChatContentProtection.IsProtected(stored)
            ? stored
            : NodeChatContentProtection.Protect(stored, NodeEncryptionKey.Span, conversationId, messageId, "metadata_json");
    }

    /// <summary>
    ///     Encrypts an uploaded file's display name for raw-SQL persistence by the conversation file store. Mirrors the
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> column posture so an EF-tracked save and this raw-SQL
    ///     write produce interchangeable ciphertext. AAD = conversationId + fileId + "original_file_name".
    /// </summary>
    public byte[] EncryptUploadedFileName(string originalFileName, Guid conversationId, Guid fileId)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);

        var plaintext = Encoding.UTF8.GetBytes(originalFileName);
        return NodePayloadProtector.Encrypt(plaintext, NodeEncryptionKey.Span, conversationId, fileId, "original_file_name");
    }

    /// <summary>
    ///     Decrypts an uploaded file's display-name blob back to a string. AAD mirrors
    ///     <see cref="EncryptUploadedFileName" />: conversationId + fileId + "original_file_name".
    /// </summary>
    public string DecryptUploadedFileName(byte[] encrypted, Guid conversationId, Guid fileId)
    {
        ArgumentNullException.ThrowIfNull(encrypted);

        var plaintext = NodePayloadProtector.Decrypt(encrypted, NodeEncryptionKey.Span, conversationId, fileId, "original_file_name");
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>
    ///     Encrypts a knowledge-base document's display name for raw-SQL persistence by the knowledge document store.
    ///     Mirrors <see cref="EncryptUploadedFileName" /> but a knowledge document has no owning conversation, so the AAD
    ///     binds to <c>(Guid.Empty, documentId, "original_file_name")</c>. The name is encrypted only on the store's
    ///     raw-SQL insert path — this column is deliberately kept out of the node-encryption interceptor.
    /// </summary>
    public byte[] EncryptKnowledgeFileName(string originalFileName, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(originalFileName);

        var plaintext = Encoding.UTF8.GetBytes(originalFileName);
        return NodePayloadProtector.Encrypt(plaintext, NodeEncryptionKey.Span, Guid.Empty, documentId, "original_file_name");
    }

    /// <summary>
    ///     Decrypts a knowledge-base document's display-name blob back to a string. AAD mirrors
    ///     <see cref="EncryptKnowledgeFileName" />: <c>(Guid.Empty, documentId, "original_file_name")</c>.
    /// </summary>
    public string DecryptKnowledgeFileName(byte[] encrypted, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(encrypted);

        var plaintext = NodePayloadProtector.Decrypt(encrypted, NodeEncryptionKey.Span, Guid.Empty, documentId, "original_file_name");
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
        modelBuilder.ApplyConfiguration(new AgentSkillResourceConfiguration());
        modelBuilder.ApplyConfiguration(new PlaybookActionConfiguration());
        modelBuilder.ApplyConfiguration(new AgentExecutionLogConfiguration());
        modelBuilder.ApplyConfiguration(new GoldenConversationConfiguration());
        modelBuilder.ApplyConfiguration(new McpServerRegistrationConfiguration());
        modelBuilder.ApplyConfiguration(new McpServerApiKeyConfiguration());
        modelBuilder.ApplyConfiguration(new ModelClassificationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelProviderMapConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobDefinitionConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunConfiguration());
        modelBuilder.ApplyConfiguration(new ScheduledJobRunEventConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitSnapshotConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitRecommendationConfiguration());
        modelBuilder.ApplyConfiguration(new ModelFitBenchmarkConfiguration());
        modelBuilder.ApplyConfiguration(new InferenceProfileConfiguration());
        modelBuilder.ApplyConfiguration(new ConversationUploadedFileConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeDocumentConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeDocumentSectionConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeDocumentChunkConfiguration());
        modelBuilder.ApplyConfiguration(new KnowledgeChunkVectorConfiguration());
        modelBuilder.ApplyConfiguration(new ImageJobConfiguration());
        modelBuilder.ApplyConfiguration(new GeneratedImageConfiguration());
        modelBuilder.ApplyConfiguration(new ImageModelProfileConfiguration());
        modelBuilder.ApplyConfiguration(new ChatMaintenanceStateConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentProjectConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentTaskConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentArtifactConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentEventConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentTemplateConfiguration());
        modelBuilder.ApplyConfiguration(new DevelopmentTemplateMaterializationConfiguration());
    }
}
