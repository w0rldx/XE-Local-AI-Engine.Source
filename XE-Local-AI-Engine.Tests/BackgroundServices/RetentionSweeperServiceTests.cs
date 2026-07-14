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

    [Test]
    public async Task OrphanResweep_WhenDisabled_RemovesOrphansButKeepsValidConversation()
    {
        await using var provider = await BuildProviderAsync("disabled-orphan.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        // A valid conversation with a real row + upload directory: it must survive because inactivity-based deletion
        // stays gated on Enabled.
        var keptConversationId = await SeedConversationWithFootprintAsync(provider, service).ConfigureAwait(false);

        // A stranded upload directory whose conversation row is gone: it must be reconciled even with retention off.
        var orphanConversationId = Guid.NewGuid();
        Directory.CreateDirectory(UploadDirectory(orphanConversationId));
        await File.WriteAllTextAsync(Path.Combine(UploadDirectory(orphanConversationId), "leftover.bin"), "x").ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: false);
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(Directory.Exists(UploadDirectory(orphanConversationId)), "A disabled sweeper must still reconcile orphaned upload directories.");
        AssertEx.NotNull(await service.GetConversationAsync(keptConversationId).ConfigureAwait(false));
        AssertEx.True(Directory.Exists(UploadDirectory(keptConversationId)), "The valid conversation's upload directory must survive when retention is disabled.");
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

    [Test]
    public async Task Sweep_WithMillisecondTimestamps_DeletesJustInsideCutoffButNotJustOutside()
    {
        // Production timestamps are Unix MILLISECONDS. The cutoff must be milliseconds too: seeding 13-digit last_seen
        // values and asserting the boundary proves the fix. Under the old seconds cutoff the ~10-digit cutoff is always
        // smaller than any real 13-digit last_seen, so nothing would ever be deleted and this test would fail.
        const long fixedNowMs = 2_000_000_000_000; // ~2033, a realistic 13-digit millisecond clock.
        var fixedClock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(fixedNowMs));
        var cutoffMs = fixedNowMs - (long)TimeSpan.FromDays(30).TotalMilliseconds;

        await using var provider = await BuildProviderAsync("ms-boundary.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        // last_seen exactly at the cutoff is eligible (predicate is last_seen <= cutoff); one millisecond newer is not.
        var justInsideId = await SeedConversationAtAsync(service, cutoffMs).ConfigureAwait(false);
        var justOutsideId = await SeedConversationAtAsync(service, cutoffMs + 1).ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: true, timeProvider: fixedClock);
        await sweeper.RunSweepOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(await service.GetConversationAsync(justInsideId).ConfigureAwait(false) is null,
            "A conversation whose last_seen is exactly at the millisecond cutoff must be deleted.");
        AssertEx.NotNull(await service.GetConversationAsync(justOutsideId).ConfigureAwait(false));
    }

    [Test]
    public async Task Sweep_ConversationTouchedAfterCandidateSelection_SurvivesWhilePeerIsDeleted()
    {
        const long fixedNowMs = 2_000_000_000_000;
        var fixedClock = new FixedTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(fixedNowMs));
        var cutoffMs = fixedNowMs - (long)TimeSpan.FromDays(30).TotalMilliseconds;

        var touchedId = Guid.Empty;
        INodeChatPersistenceService? service = null;

        // Deterministic interleave (no sleeps): the fake store wraps the real one and, the instant after candidate
        // selection returns, touches one candidate — bumping its last_seen to "now" exactly as a racing send/touch
        // would, in the window between selection and deletion. The per-candidate delete then re-checks eligibility under
        // the conversation-exclusive lock and must spare it.
        await using var provider = await BuildProviderAsync("touch-race.sqlite",
            services => services.AddScoped<INodeRetentionStore>(sp => new TouchInjectingRetentionStore(
                new NodeRetentionStore(sp.GetRequiredService<NodeChatDbContext>()),
                async () =>
                {
                    if (service is not null && touchedId != Guid.Empty)
                    {
                        await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(touchedId, IsPinned: false, UpdatedAtUtc: fixedNowMs)).ConfigureAwait(false);
                    }
                }))).ConfigureAwait(false);

        service = CreateService(provider);

        // Two conversations, both expired at selection time, each with a full footprint incl. an on-disk upload dir.
        touchedId = await SeedExpiredConversationWithFootprintAsync(provider, service, cutoffMs - 5_000).ConfigureAwait(false);
        var deletedId = await SeedExpiredConversationWithFootprintAsync(provider, service, cutoffMs - 5_000).ConfigureAwait(false);

        using var sweeper = CreateSweeper(provider, enabled: true, timeProvider: fixedClock);
        await sweeper.RunSweepOnceAsync(CancellationToken.None).ConfigureAwait(false);

        // Touched after selection: the in-transaction re-check saw the fresh last_seen and spared it — the row and its
        // upload directory both survive, proving retention cannot delete a just-touched conversation.
        AssertEx.NotNull(await service.GetConversationAsync(touchedId).ConfigureAwait(false));
        AssertEx.True(Directory.Exists(UploadDirectory(touchedId)), "A conversation touched after selection must keep its upload directory.");
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "conversation_uploaded_files", touchedId).ConfigureAwait(false));

        // Still-expired peer: deleted, and only its blobs are torn down — blob teardown targets actually-deleted ids.
        AssertEx.True(await service.GetConversationAsync(deletedId).ConfigureAwait(false) is null, "The still-expired conversation must be deleted.");
        AssertEx.False(Directory.Exists(UploadDirectory(deletedId)), "The deleted conversation's upload directory must be removed.");
    }

    // Creates a conversation with no messages, so its last_seen_utc is exactly the supplied millisecond value.
    private static async Task<Guid> SeedConversationAtAsync(INodeChatPersistenceService service, long lastSeenMs)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: lastSeenMs)).ConfigureAwait(false);
        return conversation.ConversationId;
    }

    // Creates a full-footprint conversation (message + feedback + upload dir) whose last_seen_utc is pinned to the
    // supplied millisecond value (message/feedback touches all use the same value, and the final pin makes it explicit).
    private static async Task<Guid> SeedExpiredConversationWithFootprintAsync(ServiceProvider provider, INodeChatPersistenceService service, long lastSeenMs)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: lastSeenMs)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "question", CreatedAtUtc: lastSeenMs)).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, "up", Comment: null, UpdatedAtUtc: lastSeenMs)).ConfigureAwait(false);

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

        await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(conversation.ConversationId, IsPinned: false, UpdatedAtUtc: lastSeenMs)).ConfigureAwait(false);
        return conversation.ConversationId;
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

    private async Task<ServiceProvider> BuildProviderAsync(string fileName, Action<ServiceCollection>? customize = null)
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

        // Lets an individual test replace a registration (e.g. wrap INodeRetentionStore to inject a deterministic touch
        // between candidate selection and deletion). Last registration wins, so the override supersedes the default.
        customize?.Invoke(services);

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

    private static RetentionSweeperService CreateSweeper(ServiceProvider provider, bool enabled, TimeProvider? timeProvider = null)
    {
        return new RetentionSweeperService(provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider ?? TimeProvider.System,
            provider.GetRequiredService<NodeChatPersistenceWriter>(),
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

    // A clock frozen at a fixed instant so the retention cutoff (now - RetentionDays) is deterministic.
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    // Wraps the real retention store and runs a callback immediately after candidate selection, deterministically
    // reproducing a send/touch that races in the window between candidate selection and per-candidate deletion.
    private sealed class TouchInjectingRetentionStore(INodeRetentionStore inner, Func<Task> afterSelection) : INodeRetentionStore
    {
        public async Task<IReadOnlyList<Guid>> ListExpiredConversationCandidatesAsync(long cutoffUtc, CancellationToken cancellationToken = default)
        {
            var candidates = await inner.ListExpiredConversationCandidatesAsync(cutoffUtc, cancellationToken).ConfigureAwait(false);
            await afterSelection().ConfigureAwait(false);
            return candidates;
        }
    }
}
