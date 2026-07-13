namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class RetentionSweeperServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public void ChatRetentionOptions_IsDisabledByDefault()
    {
        var options = new ChatRetentionOptions();
        AssertEx.False(options.Enabled, "Chat retention must be disabled by default.");
        AssertEx.Equal(expected: 30, options.RetentionDays);
    }

    [Test]
    public async Task RetentionOptions_ZeroRetentionDays_FailStartupValidation()
    {
        // A zero-day window makes the sweep cutoff equal to now, deleting every conversation the instant retention is
        // enabled; ValidateOnStart must reject it at startup rather than silently purging everything.
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "0", sweepInterval: null))
                      .ConfigureAwait(false);
    }

    [Test]
    public async Task RetentionOptions_NegativeRetentionDays_FailStartupValidation()
    {
        // A negative window pushes the cutoff into the future, which would also delete everything.
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "-5", sweepInterval: null))
                      .ConfigureAwait(false);
    }

    [Test]
    public async Task RetentionOptions_ZeroSweepInterval_FailStartupValidation()
    {
        // A zero sweep interval would busy-spin the PeriodicTimer; the interval bounds must reject it at startup.
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "30", sweepInterval: "00:00:00"))
                      .ConfigureAwait(false);
    }

    [Test]
    public async Task RetentionOptions_ValidConfig_PassesStartupValidation()
    {
        await StartHostWithRetentionAsync(enabled: true, retentionDays: "30", sweepInterval: "00:10:00").ConfigureAwait(false);
    }

    [Test]
    public async Task RetentionOptions_DisabledWithDefaults_PassesStartupValidation()
    {
        // The default-off configuration (no overrides) must never fail startup validation.
        await StartHostWithRetentionAsync(enabled: false, retentionDays: null, sweepInterval: null).ConfigureAwait(false);
    }

    // Builds a host that registers ChatRetentionOptions exactly as production does (bind + ValidateDataAnnotations +
    // ValidateOnStart) and starts it, so a bad window surfaces as an OptionsValidationException during StartAsync.
    private static async Task StartHostWithRetentionAsync(bool enabled, string? retentionDays, string? sweepInterval)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{ChatRetentionOptions.Section}:Enabled"] = enabled ? "true" : "false"
        };
        if (retentionDays is not null)
        {
            settings[$"{ChatRetentionOptions.Section}:RetentionDays"] = retentionDays;
        }

        if (sweepInterval is not null)
        {
            settings[$"{ChatRetentionOptions.Section}:SweepInterval"] = sweepInterval;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddOptions<ChatRetentionOptions>()
               .Bind(builder.Configuration.GetSection(ChatRetentionOptions.Section))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        using var host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);
        await host.StopAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task Sweep_WhenDisabled_DeletesNothing()
    {
        await using var provider = await BuildProviderAsync("disabled.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service).ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: false);
        await sweeper.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await sweeper.StopAsync(CancellationToken.None).ConfigureAwait(false);

        // Nothing is deleted: the conversation, its feedback row, and its upload directory all survive.
        AssertEx.NotNull(await service.GetConversationAsync(conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "message_feedback", conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId).ConfigureAwait(false));
        AssertEx.True(Directory.Exists(UploadDirectory(conversationId)), "The upload directory must survive when retention is disabled.");
    }

    [Test]
    public async Task Sweep_WhenEnabled_DeletesFullFootprintIncludingFeedbackUploadsAndBlobs()
    {
        await using var provider = await BuildProviderAsync("enabled.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service).ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: true);
        await sweeper.RunSweepOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(await service.GetConversationAsync(conversationId).ConfigureAwait(false) is null, "The expired conversation must be deleted.");
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "message_feedback", conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "messages", conversationId).ConfigureAwait(false));
        AssertEx.False(Directory.Exists(UploadDirectory(conversationId)), "The on-disk upload directory must be deleted.");
    }

    [Test]
    public async Task Sweep_OrphanResweep_RemovesUploadDirectoryWithNoConversationRow()
    {
        await using var provider = await BuildProviderAsync("orphan.sqlite").ConfigureAwait(false);

        // Simulate a crash between a purge's DB commit and its blob teardown: an upload directory exists for a
        // conversation that has no row.
        var orphanConversationId = Guid.NewGuid();
        Directory.CreateDirectory(UploadDirectory(orphanConversationId));
        await File.WriteAllTextAsync(Path.Combine(UploadDirectory(orphanConversationId), "leftover.bin"), "x").ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: true);
        await sweeper.RunSweepOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(Directory.Exists(UploadDirectory(orphanConversationId)), "An upload directory with no conversation row must be resweept away.");
    }

    [Test]
    public async Task InteractivePurge_StillDeletesTheCompleteFootprint()
    {
        await using var provider = await BuildProviderAsync("interactive-purge.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service).ConfigureAwait(false);

        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversationId, DeletedAtUtc: 100, PurgeImmediately: true)).ConfigureAwait(false);

        AssertEx.True(await service.GetConversationAsync(conversationId).ConfigureAwait(false) is null, "The purged conversation must be deleted.");
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "message_feedback", conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId).ConfigureAwait(false));
        AssertEx.False(Directory.Exists(UploadDirectory(conversationId)), "Interactive purge must also delete the on-disk upload directory.");
    }

    private static async Task<Guid> SeedConversationWithFootprintAsync(ServiceProvider provider, INodeChatPersistenceService service)
    {
        // Small timestamps put last_seen far in the past, so the conversation is always expired against a real-now
        // retention cutoff.
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "question", CreatedAtUtc: 2)).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, "up", Comment: null, UpdatedAtUtc: 3)).ConfigureAwait(false);

        var uploadedFileStore = provider.GetRequiredService<IConversationUploadedFileStore>();
        await uploadedFileStore.AddAsync(new ConversationUploadedFileInput(conversation.ConversationId,
                Guid.NewGuid(),
                "doc.txt",
                "text/plain",
                ".txt",
                SizeBytes: 4,
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("data")),
                DocumentExtractionStatus.Extracted,
                ExtractedMarkdown: "data",
                ExtractedChars: 4),
            CancellationToken.None).ConfigureAwait(false);

        return conversation.ConversationId;
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);

        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(_rootPath));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConversationUploadedFileStore, ConversationUploadedFileStore>();
        services.AddScoped<INodeRetentionStore, NodeRetentionStore>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(),
            provider.GetRequiredService<IConversationUploadedFileStore>());
    }

    private static RetentionSweeperService CreateSweeper(ServiceProvider provider, bool enabled)
    {
        return new RetentionSweeperService(provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            Options.Create(new ChatRetentionOptions { Enabled = enabled, RetentionDays = 30, SweepInterval = TimeSpan.FromMinutes(10) }),
            NullLogger<RetentionSweeperService>.Instance);
    }

    private string UploadDirectory(Guid conversationId)
    {
        return Path.Combine(_rootPath, "uploaded-files", "conversations", conversationId.ToString("D"));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The table name is a hardcoded test constant, never user input; the conversation id is a parameter.")]
    private static async Task<int> CountRowsAsync(ServiceProvider provider, string table, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE conversation_id = $conversation_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversation_id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
