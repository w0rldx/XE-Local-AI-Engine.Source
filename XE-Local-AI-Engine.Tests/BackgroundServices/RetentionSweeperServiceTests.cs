namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.WorkSessions;

[Category(TestCategories.Integration)]
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
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "0", sweepInterval: null));
    }

    [Test]
    public async Task RetentionOptions_NegativeRetentionDays_FailStartupValidation()
    {
        // A negative window pushes the cutoff into the future, which would also delete everything.
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "-5", sweepInterval: null));
    }

    [Test]
    public async Task RetentionOptions_ZeroSweepInterval_FailStartupValidation()
    {
        // A zero sweep interval would busy-spin the PeriodicTimer; the interval bounds must reject it at startup.
        await AssertEx.ThrowsAsync<OptionsValidationException>(() => StartHostWithRetentionAsync(enabled: true, retentionDays: "30", sweepInterval: "00:00:00"));
    }

    [Test]
    public async Task RetentionOptions_ValidConfig_PassesStartupValidation()
    {
        await StartHostWithRetentionAsync(enabled: true, retentionDays: "30", sweepInterval: "00:10:00");
    }

    [Test]
    public async Task RetentionOptions_DisabledWithDefaults_PassesStartupValidation()
    {
        // The default-off configuration (no overrides) must never fail startup validation.
        await StartHostWithRetentionAsync(enabled: false, retentionDays: null, sweepInterval: null);
    }

    [Test]
    public async Task OrphanResweep_WhenDisabled_RemovesOrphansButKeepsValidConversation()
    {
        await using var provider = await BuildProviderAsync("disabled-orphan.sqlite");
        var service = CreateService(provider);

        // A valid conversation with a real row + upload directory: it must survive because inactivity-based deletion
        // stays gated on Enabled.
        var keptConversationId = await SeedConversationWithFootprintAsync(provider, service);

        // A stranded upload directory whose conversation row is gone: it must be reconciled even with retention off.
        var orphanConversationId = Guid.NewGuid();
        Directory.CreateDirectory(UploadDirectory(orphanConversationId));
        await File.WriteAllTextAsync(Path.Combine(UploadDirectory(orphanConversationId), "leftover.bin"), "x");

        using var sweeper = CreateSweeper(provider, enabled: false);
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(UploadDirectory(orphanConversationId)), "A disabled sweeper must still reconcile orphaned upload directories.");
        AssertEx.NotNull(await service.GetConversationAsync(keptConversationId));
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
        await host.StartAsync();
        await host.StopAsync();
    }

    [Test]
    public async Task Sweep_WhenDisabled_DeletesNothing()
    {
        await using var provider = await BuildProviderAsync("disabled.sqlite");
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service);

        using var sweeper = CreateSweeper(provider, enabled: false);
        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);

        // Nothing is deleted: the conversation, its feedback row, and its upload directory all survive.
        AssertEx.NotNull(await service.GetConversationAsync(conversationId));
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "message_feedback", conversationId));
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId));
        AssertEx.True(Directory.Exists(UploadDirectory(conversationId)), "The upload directory must survive when retention is disabled.");
    }

    [Test]
    public async Task Sweep_WhenEnabled_DeletesFullFootprintIncludingFeedbackUploadsAndBlobs()
    {
        await using var provider = await BuildProviderAsync("enabled.sqlite");
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service);

        using var sweeper = CreateSweeper(provider, enabled: true);
        await sweeper.RunSweepOnceAsync(CancellationToken.None);

        AssertEx.True(await service.GetConversationAsync(conversationId) is null, "The expired conversation must be deleted.");
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "message_feedback", conversationId));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "messages", conversationId));
        AssertEx.False(Directory.Exists(UploadDirectory(conversationId)), "The on-disk upload directory must be deleted.");
    }

    [Test]
    public async Task Sweep_OrphanResweep_RemovesUploadDirectoryWithNoConversationRow()
    {
        await using var provider = await BuildProviderAsync("orphan.sqlite");

        // Simulate a crash between a purge's DB commit and its blob teardown: an upload directory exists for a
        // conversation that has no row.
        var orphanConversationId = Guid.NewGuid();
        Directory.CreateDirectory(UploadDirectory(orphanConversationId));
        await File.WriteAllTextAsync(Path.Combine(UploadDirectory(orphanConversationId), "leftover.bin"), "x");

        using var sweeper = CreateSweeper(provider, enabled: true);
        await sweeper.RunSweepOnceAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(UploadDirectory(orphanConversationId)), "An upload directory with no conversation row must be resweept away.");
    }

    [Test]
    public async Task InteractivePurge_StillDeletesTheCompleteFootprint()
    {
        await using var provider = await BuildProviderAsync("interactive-purge.sqlite");
        var service = CreateService(provider);
        var conversationId = await SeedConversationWithFootprintAsync(provider, service);

        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest { ConversationId = conversationId, DeletedAtUtc = 100, PurgeImmediately = true });

        AssertEx.True(await service.GetConversationAsync(conversationId) is null, "The purged conversation must be deleted.");
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "message_feedback", conversationId));
        AssertEx.Equal(expected: 0, await CountRowsAsync(provider, "conversation_uploaded_files", conversationId));
        AssertEx.False(Directory.Exists(UploadDirectory(conversationId)), "Interactive purge must also delete the on-disk upload directory.");
    }

    [Test]
    public async Task Sweep_WhenEnabled_DeletesWorkSessionArtifactBlobs()
    {
        // A work session's artifact BYTES live on disk under the session id, so the conversation row purge cannot
        // reach them. Retention must tear them down the same way it tears down upload blobs, or ageing out the
        // conversation leaves the session's content readable on disk forever.
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var artifactDirectory = await WriteArtifactBlobAsync(factory.Services, sessionId);

        using var sweeper = CreateSweeper(factory.Services, enabled: true);
        await sweeper.RunSweepOnceAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        AssertEx.True(await chat.GetConversationAsync(session.ConversationId) is null, "The expired session conversation must be deleted.");
        AssertEx.False(Directory.Exists(artifactDirectory), "Retention must delete the artifact bytes of a session whose conversation it aged out.");
    }

    [Test]
    public async Task InteractivePurge_AlsoDeletesWorkSessionArtifactBlobs()
    {
        // Same footprint, the other entry point: an immediate purge from the chat surface.
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var artifactDirectory = await WriteArtifactBlobAsync(factory.Services, sessionId);

        await using var scope = factory.Services.CreateAsyncScope();
        var chat = scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        _ = await chat.DeleteConversationAsync(new NodeChatDeleteConversationRequest { ConversationId = session.ConversationId, DeletedAtUtc = 100, PurgeImmediately = true });

        AssertEx.True(await chat.GetConversationAsync(session.ConversationId) is null, "The purged conversation must be deleted.");
        AssertEx.False(Directory.Exists(artifactDirectory), "An immediate purge must also delete the session's artifact bytes.");
    }

    [Test]
    public async Task OrphanResweep_ReclaimsAnArtifactScopeWhoseOwningRowIsGone()
    {
        // What a crash between the row commit and the best-effort DeleteSession/DeleteRun beside it strands for good:
        // artifact bytes under a scope id no row names any more. Both consumers that can delete a row are swept.
        await using var provider = await BuildProviderAsync("orphan-scopes.sqlite", RegisterDevWorkflowArtifactBlobStore);
        var orphanedSession = SeedArtifactScope("work-sessions", Guid.NewGuid(), AgedWriteTimeUtc());
        var orphanedRun = SeedArtifactScope("dev-workflows", Guid.NewGuid(), AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(provider, enabled: false, timeProvider: new FixedTimeProvider(FixedNowUtc));
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(orphanedSession), "A work-session artifact directory with no agent_work_sessions row must be reclaimed.");
        AssertEx.False(Directory.Exists(orphanedRun), "A workflow-run artifact directory with no dev_workflow_runs row must be reclaimed.");
    }

    [Test]
    public async Task OrphanResweep_SparesAnArtifactScopeWrittenInsideTheGraceWindow()
    {
        // The creation race: the scope directory is listed from disk and its row probed a moment later, so a scope
        // whose first artifact was just written must survive even though nothing has committed its row yet here.
        await using var provider = await BuildProviderAsync("orphan-scope-grace.sqlite");
        var freshScope = SeedArtifactScope("work-sessions", Guid.NewGuid(), FreshWriteTimeUtc());
        var agedScope = SeedArtifactScope("work-sessions", Guid.NewGuid(), AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(provider, enabled: false, timeProvider: new FixedTimeProvider(FixedNowUtc));
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(freshScope), "A scope written inside the grace window may still be racing its own row commit.");
        AssertEx.False(Directory.Exists(agedScope), "The aged peer proves the sweep ran, so the spared scope is not spared by a no-op.");
    }

    [Test]
    public async Task OrphanResweep_SparesTheArtifactScopeOfALiveSession()
    {
        await using var factory = WorkSessionServiceTests.NewFactory();
        var sessionId = Guid.NewGuid();
        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId);
        var artifactDirectory = await WriteArtifactBlobAsync(factory.Services, sessionId);
        AgeDirectory(artifactDirectory, AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(factory.Services, enabled: false, timeProvider: new FixedTimeProvider(FixedNowUtc));
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(artifactDirectory), "A live session's artifact bytes must survive the orphan resweep however old they are.");
    }

    [Test]
    public async Task OrphanResweep_DoesNotFollowOrDeleteASymlinkedArtifactScope()
    {
        SymlinkSupport.EnsureSupported();

        await using var provider = await BuildProviderAsync("orphan-scope-symlink.sqlite");
        var target = Path.Combine(_rootPath, "outside-the-store");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep-me.txt"), "not the node's to delete");
        var linkPath = Path.Combine(_rootPath, "work-sessions", "artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Directory.CreateSymbolicLink(linkPath, target);
        var agedScope = SeedArtifactScope("work-sessions", Guid.NewGuid(), AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(provider, enabled: false, timeProvider: new FixedTimeProvider(FixedNowUtc));
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(linkPath), "A symbolic link planted in the store is never a sweep candidate.");
        AssertEx.True(File.Exists(Path.Combine(target, "keep-me.txt")), "A recursive delete must never reach through a link to what it points at.");
        AssertEx.False(Directory.Exists(agedScope), "The aged peer proves the sweep ran, so the link is not spared by a no-op.");
    }

    [Test]
    public async Task OrphanResweep_WhenOneArtifactScopeDeleteFails_StillReclaimsTheRest()
    {
        var failingSessionId = Guid.NewGuid();
        await using var provider = await BuildProviderAsync("orphan-scope-failure.sqlite",
            services => services.AddSingleton<IWorkSessionArtifactBlobStore>(serviceProvider =>
                new ThrowingDeleteArtifactBlobStore(ActivatorUtilities.CreateInstance<ManagedWorkSessionArtifactBlobStore>(serviceProvider), failingSessionId)));
        var failingScope = SeedArtifactScope("work-sessions", failingSessionId, AgedWriteTimeUtc());
        var secondOrphan = SeedArtifactScope("work-sessions", Guid.NewGuid(), AgedWriteTimeUtc());

        using var sweeper = CreateSweeper(provider, enabled: false, timeProvider: new FixedTimeProvider(FixedNowUtc));
        await sweeper.RunOrphanResweepOnceAsync(CancellationToken.None);

        AssertEx.True(Directory.Exists(failingScope), "The scope whose delete threw is left for the next start.");
        AssertEx.False(Directory.Exists(secondOrphan), "A failure on one scope must not stop the sweep reclaiming the rest.");
    }

    private static void RegisterDevWorkflowArtifactBlobStore(ServiceCollection services)
    {
        services.AddSingleton(Options.Create(new DevWorkflowOptions()));
        services.AddSingleton<IDevWorkflowArtifactBlobStore, ManagedDevWorkflowArtifactBlobStore>();
    }

    // Puts artifact bytes where a scope directory sits, with every stamp the age gate reads pinned to one instant so
    // "aged" and "fresh" are properties of the fixture rather than of how long the test took.
    private string SeedArtifactScope(string folderSegment, Guid scopeId, DateTime lastWriteUtc)
    {
        var directory = Path.Combine(_rootPath, folderSegment, "artifacts", scopeId.ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, string.Concat(Guid.NewGuid().ToString("N"), ".blob")), "encrypted artifact bytes");
        AgeDirectory(directory, lastWriteUtc);
        return directory;
    }

    private static void AgeDirectory(string directory, DateTime lastWriteUtc)
    {
        foreach (var file in Directory.GetFiles(directory))
        {
            File.SetLastWriteTimeUtc(file, lastWriteUtc);
        }

        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
    }

    // The instant the artifact-scope sweeps read as "now"; the grace window is 15 minutes, so an hour back is settled
    // litter and a minute back is a write that may still be running.
    private static readonly DateTimeOffset FixedNowUtc = new(year: 2026, month: 9, day: 21, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    private static DateTime AgedWriteTimeUtc() =>
        FixedNowUtc.AddHours(-1).UtcDateTime;

    private static DateTime FreshWriteTimeUtc() =>
        FixedNowUtc.AddMinutes(-1).UtcDateTime;

    // Wraps the real store and fails the scope delete of one nominated session, reproducing a directory the OS refuses
    // to remove without faking the listing or the row probe around it.
    private sealed class ThrowingDeleteArtifactBlobStore : IWorkSessionArtifactBlobStore
    {
        private readonly IWorkSessionArtifactBlobStore _inner;
        private readonly Guid _failingSessionId;

        public ThrowingDeleteArtifactBlobStore(IWorkSessionArtifactBlobStore inner, Guid failingSessionId)
        {
            _inner = inner;
            _failingSessionId = failingSessionId;
        }

        public Task<WorkSessionArtifactBlobWriteResult> WriteAsync(Guid sessionId, Guid artifactId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(sessionId, artifactId, content, cancellationToken);

        public Task<WorkSessionArtifactBlobReadResult> ReadAsync(Guid sessionId, Guid artifactId, string expectedHash, long expectedByteCount, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(sessionId, artifactId, expectedHash, expectedByteCount, cancellationToken);

        public void Delete(Guid sessionId, Guid artifactId) =>
            _inner.Delete(sessionId, artifactId);

        public void DeleteSession(Guid sessionId)
        {
            if (sessionId == _failingSessionId)
            {
                throw new IOException("The directory is in use by another process.");
            }

            _inner.DeleteSession(sessionId);
        }

        public IReadOnlyList<Guid> ListSessionIdsLastWrittenBefore(DateTimeOffset cutoffUtc) =>
            _inner.ListSessionIdsLastWrittenBefore(cutoffUtc);
    }

    // Writes one artifact through the real blob store and returns the session's on-disk directory, asserting it exists
    // so a later "gone" assertion cannot pass vacuously.
    private static async Task<string> WriteArtifactBlobAsync(IServiceProvider services, Guid sessionId)
    {
        var blobStore = services.GetRequiredService<IWorkSessionArtifactBlobStore>();
        _ = await blobStore.WriteAsync(sessionId, Guid.NewGuid(), new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("artifact bytes")), CancellationToken.None);

        var directory = Path.Combine(services.GetRequiredService<INodeDataDirectory>().Root, "work-sessions", "artifacts", sessionId.ToString("N"));
        AssertEx.True(Directory.Exists(directory), "The seeded artifact directory must exist before the purge.");
        return directory;
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

        await using var provider = await BuildProviderAsync("ms-boundary.sqlite");
        var service = CreateService(provider);

        // last_seen exactly at the cutoff is eligible (predicate is last_seen <= cutoff); one millisecond newer is not.
        var justInsideId = await SeedConversationAtAsync(service, cutoffMs);
        var justOutsideId = await SeedConversationAtAsync(service, cutoffMs + 1);

        using var sweeper = CreateSweeper(provider, enabled: true, timeProvider: fixedClock);
        await sweeper.RunSweepOnceAsync(CancellationToken.None);

        AssertEx.True(await service.GetConversationAsync(justInsideId) is null,
            "A conversation whose last_seen is exactly at the millisecond cutoff must be deleted.");
        AssertEx.NotNull(await service.GetConversationAsync(justOutsideId));
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
            services => services.AddScoped<INodeRetentionStore>(sp => new TouchInjectingRetentionStore(new NodeRetentionStore(sp.GetRequiredService<NodeChatDbContext>()),
                async () =>
                {
                    if (service is not null && touchedId != Guid.Empty)
                    {
                        await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest { ConversationId = touchedId, IsPinned = false, UpdatedAtUtc = fixedNowMs });
                    }
                })));

        service = CreateService(provider);

        // Two conversations, both expired at selection time, each with a full footprint incl. an on-disk upload dir.
        touchedId = await SeedExpiredConversationWithFootprintAsync(provider, service, cutoffMs - 5_000);
        var deletedId = await SeedExpiredConversationWithFootprintAsync(provider, service, cutoffMs - 5_000);

        using var sweeper = CreateSweeper(provider, enabled: true, timeProvider: fixedClock);
        await sweeper.RunSweepOnceAsync(CancellationToken.None);

        // Touched after selection: the in-transaction re-check saw the fresh last_seen and spared it — the row and its
        // upload directory both survive, proving retention cannot delete a just-touched conversation.
        AssertEx.NotNull(await service.GetConversationAsync(touchedId));
        AssertEx.True(Directory.Exists(UploadDirectory(touchedId)), "A conversation touched after selection must keep its upload directory.");
        AssertEx.Equal(expected: 1, await CountRowsAsync(provider, "conversation_uploaded_files", touchedId));

        // Still-expired peer: deleted, and only its blobs are torn down — blob teardown targets actually-deleted ids.
        AssertEx.True(await service.GetConversationAsync(deletedId) is null, "The still-expired conversation must be deleted.");
        AssertEx.False(Directory.Exists(UploadDirectory(deletedId)), "The deleted conversation's upload directory must be removed.");
    }

    // Creates a conversation with no messages, so its last_seen_utc is exactly the supplied millisecond value.
    private static async Task<Guid> SeedConversationAtAsync(INodeChatPersistenceService service, long lastSeenMs)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Chat", UserId = "node", CreatedAtUtc = lastSeenMs });
        return conversation.ConversationId;
    }

    // Creates a full-footprint conversation (message + feedback + upload dir) whose last_seen_utc is pinned to the
    // supplied millisecond value (message/feedback touches all use the same value, and the final pin makes it explicit).
    private static async Task<Guid> SeedExpiredConversationWithFootprintAsync(ServiceProvider provider, INodeChatPersistenceService service, long lastSeenMs)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Chat", UserId = "node", CreatedAtUtc = lastSeenMs });
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest { ConversationId = conversation.ConversationId, MessageId = messageId, Content = "question", CreatedAtUtc = lastSeenMs });
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest { ConversationId = conversation.ConversationId, MessageId = messageId, Rating = "up", Comment = null, UpdatedAtUtc = lastSeenMs });

        var uploadedFileStore = provider.GetRequiredService<IConversationUploadedFileStore>();
        await uploadedFileStore.AddAsync(new ConversationUploadedFileInput
        {
            ConversationId = conversation.ConversationId,
            FileId = Guid.NewGuid(),
            OriginalFileName = "doc.txt",
            MimeType = "text/plain",
            Extension = ".txt",
            SizeBytes = 4,
            Content = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("data")),
            ExtractionStatus = DocumentExtractionStatus.Extracted,
            ExtractedMarkdown = "data",
            ExtractedChars = 4
        },
            CancellationToken.None);

        await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest { ConversationId = conversation.ConversationId, IsPinned = false, UpdatedAtUtc = lastSeenMs });
        return conversation.ConversationId;
    }

    private static async Task<Guid> SeedConversationWithFootprintAsync(ServiceProvider provider, INodeChatPersistenceService service)
    {
        // Small timestamps put last_seen far in the past, so the conversation is always expired against a real-now
        // retention cutoff.
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest { Title = "Chat", UserId = "node", CreatedAtUtc = 1 });
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest { ConversationId = conversation.ConversationId, MessageId = messageId, Content = "question", CreatedAtUtc = 2 });
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest { ConversationId = conversation.ConversationId, MessageId = messageId, Rating = "up", Comment = null, UpdatedAtUtc = 3 });

        var uploadedFileStore = provider.GetRequiredService<IConversationUploadedFileStore>();
        await uploadedFileStore.AddAsync(new ConversationUploadedFileInput
        {
            ConversationId = conversation.ConversationId,
            FileId = Guid.NewGuid(),
            OriginalFileName = "doc.txt",
            MimeType = "text/plain",
            Extension = ".txt",
            SizeBytes = 4,
            Content = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("data")),
            ExtractionStatus = DocumentExtractionStatus.Extracted,
            ExtractedMarkdown = "data",
            ExtractedChars = 4
        },
            CancellationToken.None);

        return conversation.ConversationId;
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName, Action<ServiceCollection>? customize = null)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);

        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddScoped<IConversationUploadedFileRowStore, ConversationUploadedFileRowStore>();
        services.AddSingleton<NodeChatPersistenceWriter>();
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(_rootPath));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConversationUploadedFileStore, ConversationUploadedFileStore>();
        services.AddScoped<INodeRetentionStore, NodeRetentionStore>();

        // The sweeper also tears down work-session artifact bytes. These hand-rolled compositions own no session, so
        // the store is a substitute that finds none; the two work-session tests use the real host instead.
        services.AddSingleton(Options.Create(new WorkSessionOptions()));
        services.AddSingleton<IWorkSessionArtifactBlobStore, ManagedWorkSessionArtifactBlobStore>();
        services.AddScoped(_ => Substitute.For<IAgentWorkSessionStore>());

        // Lets an individual test replace a registration (e.g. wrap INodeRetentionStore to inject a deterministic touch
        // between candidate selection and deletion). Last registration wins, so the override supersedes the default.
        customize?.Invoke(services);

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(),
            provider.GetRequiredService<IConversationUploadedFileStore>());
    }

    private static RetentionSweeperService CreateSweeper(IServiceProvider provider, bool enabled, TimeProvider? timeProvider = null)
    {
        return new RetentionSweeperService(provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider ?? TimeProvider.System,
            provider.GetRequiredService<NodeChatPersistenceWriter>(),
            Options.Create(new ChatRetentionOptions
            {
                Enabled = enabled,
                RetentionDays = 30,
                SweepInterval = TimeSpan.FromMinutes(10)
            }),
            NullLogger<RetentionSweeperService>.Instance);
    }

    private string UploadDirectory(Guid conversationId)
    {
        return Path.Combine(_rootPath, "uploaded-files", "conversations", conversationId.ToString("D"));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The table name is a hardcoded test constant, never user input; the conversation id is a parameter.")]
    private static async Task<int> CountRowsAsync(ServiceProvider provider, string table, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE conversation_id = $conversation_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversation_id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    // A clock frozen at a fixed instant so the retention cutoff (now - RetentionDays) is deterministic.
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    // Wraps the real retention store and runs a callback immediately after candidate selection, deterministically
    // reproducing a send/touch that races in the window between candidate selection and per-candidate deletion.
    private sealed class TouchInjectingRetentionStore : INodeRetentionStore
    {
        private readonly INodeRetentionStore _inner;
        private readonly Func<Task> _afterSelection;

        public TouchInjectingRetentionStore(INodeRetentionStore inner, Func<Task> afterSelection)
        {
            _inner = inner;
            _afterSelection = afterSelection;
        }

        public async Task<IReadOnlyList<Guid>> ListExpiredConversationCandidatesAsync(long cutoffUtc, CancellationToken cancellationToken = default)
        {
            var candidates = await _inner.ListExpiredConversationCandidatesAsync(cutoffUtc, cancellationToken);
            await _afterSelection();
            return candidates;
        }
    }
}
