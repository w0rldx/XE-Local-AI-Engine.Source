namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Client.Services.Transcription.Live;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Rows a dead process left Transcribing are failed at startup with the interrupted reason, every other row is left alone,
///     and a graceful stop ends a real live session while the container can still persist it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionSessionLifecycleServiceTests : IDisposable
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
    public async Task StartAsync_FailsOnlyTranscribingRows()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var createdId = await SeedAsync(scopeFactory, TranscriptionSessionStatus.Created);
        var batchId = await SeedAsync(scopeFactory, TranscriptionSessionStatus.Transcribing);
        var liveId = await SeedAsync(scopeFactory, TranscriptionSessionStatus.Transcribing, TranscriptionSourceKind.Microphone);
        var completedId = await SeedAsync(scopeFactory, TranscriptionSessionStatus.Completed);

        await NewService(scopeFactory, Substitute.For<ILiveTranscriptionSessionRegistry>()).StartAsync(CancellationToken.None);

        foreach (var interruptedId in new[] { batchId, liveId })
        {
            var row = AssertEx.NotNull(await GetAsync(scopeFactory, interruptedId));
            AssertEx.Equal(TranscriptionSessionStatus.Failed, row.Status);
            AssertEx.Equal(TranscriptionSessionLifecycleService.InterruptedErrorCode, row.ErrorCode);
            AssertEx.Equal(TranscriptionSessionLifecycleService.InterruptedReason, row.ErrorMessage);
        }

        AssertEx.Equal(TranscriptionSessionStatus.Created, AssertEx.NotNull(await GetAsync(scopeFactory, createdId)).Status);
        AssertEx.Equal(TranscriptionSessionStatus.Completed, AssertEx.NotNull(await GetAsync(scopeFactory, completedId)).Status);
    }

    [Test]
    public async Task StartAsync_FailsTranscribingRowsOnEveryPage()
    {
        await using var provider = await BuildProviderAsync();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // One page is 200 rows; every tenth of 205 rows is Transcribing, so they sit on both pages.
        var transcribing = new List<Guid>();
        for (var index = 0; index < 205; index++)
        {
            var status = index % 10 == 0 ? TranscriptionSessionStatus.Transcribing : TranscriptionSessionStatus.Completed;
            var id = await SeedAsync(scopeFactory, status, createdAtUtc: index + 1);
            if (status == TranscriptionSessionStatus.Transcribing)
            {
                transcribing.Add(id);
            }
        }

        await NewService(scopeFactory, Substitute.For<ILiveTranscriptionSessionRegistry>()).StartAsync(CancellationToken.None);

        foreach (var id in transcribing)
        {
            AssertEx.Equal(TranscriptionSessionStatus.Failed, AssertEx.NotNull(await GetAsync(scopeFactory, id)).Status);
        }
    }

    [Test]
    public async Task StopAsync_WithAnOpenLiveSession_LeavesTheRowCancelledAfterTheContainerIsDisposed()
    {
        // The real registry and service in one container, as in the host: without StopAsync the container's own disposal
        // ends the session after the provider is gone, the store scope cannot be opened, and the row stays Transcribing.
        Guid sessionId;
        await using (var host = await BuildProviderAsync(AddLiveTranscription))
        {
            var service = host.GetRequiredService<ITranscriptionService>();
            var created = await service.CreateSessionAsync(new CreateTranscriptionSessionInput
            {
                SourceKind = nameof(TranscriptionSourceKind.Microphone)
            }, CancellationToken.None);
            sessionId = created.Id;
            AssertEx.Equal(StartLiveOutcome.Started, (await service.StartLiveAsync(sessionId, CancellationToken.None)).Outcome);

            await NewService(host.GetRequiredService<IServiceScopeFactory>(), host.GetRequiredService<ILiveTranscriptionSessionRegistry>())
                .StopAsync(CancellationToken.None);
        }

        await using var reader = await BuildProviderAsync();
        var row = AssertEx.NotNull(await GetAsync(reader.GetRequiredService<IServiceScopeFactory>(), sessionId));
        AssertEx.Equal(TranscriptionSessionStatus.Cancelled, row.Status);
    }

    private void AddLiveTranscription(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new TranscriptionOptions()));
        services.AddSingleton<IWhisperTranscriber>(new FakeWhisperTranscriber());
        services.AddSingleton<IWhisperServerSupervisor>(new FakeWhisperServerSupervisor());
        services.AddSingleton<IAudioTranscoder>(new FakeAudioTranscoder());
        services.AddSingleton<ITranscriptionRuntimeService>(new FakeTranscriptionRuntimeService("ggml-tiny"));
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(_rootPath));
        services.AddSingleton<ITranscriptionEventPublisher, NullTranscriptionEventPublisher>();
        services.AddSingleton<ILiveTranscriptionSessionRegistry, LiveTranscriptionSessionRegistry>();
        services.AddSingleton<ITranscriptionService, TranscriptionService>();
    }

    private static TranscriptionSessionLifecycleService NewService(IServiceScopeFactory scopeFactory, ILiveTranscriptionSessionRegistry registry) =>
        new(scopeFactory, registry, TimeProvider.System, NullLogger<TranscriptionSessionLifecycleService>.Instance);

    private static async Task<Guid> SeedAsync(IServiceScopeFactory scopeFactory,
        TranscriptionSessionStatus status,
        TranscriptionSourceKind sourceKind = TranscriptionSourceKind.File,
        long createdAtUtc = 1)
    {
        var id = Guid.NewGuid();
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
        await store.CreateAsync(new TranscriptionSessionCreate
        {
            Id = id,
            Title = "a lifecycle session",
            SourceKind = sourceKind,
            ModelId = "small",
            ConfigJson = "{}",
            CreatedAtUtc = createdAtUtc
        }, CancellationToken.None);
        _ = await store.SetStatusAsync(id, status, updatedAtUtc: 2, CancellationToken.None);
        return id;
    }

    private static async Task<TranscriptionSessionDetailView?> GetAsync(IServiceScopeFactory scopeFactory, Guid id)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>().GetWithSegmentsAsync(id, CancellationToken.None);
    }

    private async Task<ServiceProvider> BuildProviderAsync(Action<IServiceCollection>? configure = null)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_rootPath, "transcription.sqlite")}"));
        services.AddScoped<ITranscriptionSessionStore, TranscriptionSessionStore>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.EnsureCreatedAsync();
        return provider;
    }
}
