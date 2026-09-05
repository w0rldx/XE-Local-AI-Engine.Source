namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;

internal sealed class DevelopmentTestFixture : IDisposable
{
    public static readonly Guid SelectedFolderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-persistence-" + Guid.NewGuid().ToString("N"));
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public async Task<ServiceProvider> BuildProviderAsync(IDevelopmentHostApplyPort? port = null)
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".sqlite");
        var services = new ServiceCollection();
        services.AddSingleton<INodeSqliteKeyHolder>(_keyHolder);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((provider, options) => options.UseSqlite($"Data Source={databasePath}")
                                                                               .EnableServiceProviderCaching(false)

                                                                               // Every test in this suite builds its own isolated database and its own interceptor
                                                                               // pair, so each one keys a fresh EF internal provider and the twenty-provider cap is
                                                                               // process-global — it is configured as an error solution-wide. Same suppression the
                                                                               // knowledge, scheduler and encryption fixtures already carry, for the same reason.
                                                                               .ConfigureWarnings(static warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                                                                               .AddInterceptors(provider.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                                                                                   provider.GetRequiredService<NodeEncryptionMaterializationInterceptor>()));
        services.AddScoped<IDevelopmentStore, DevelopmentStore>();
        services.AddScoped<IDevelopmentHostApplyPort>(_ => port ?? new TestApplyPort());
        services.AddScoped<IDevelopmentCoordinator, DevelopmentCoordinator>();
        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        dbContext.SelectedFolders.Add(new NodeSelectedFolder
        {
            Id = SelectedFolderId,
            Alias = "development-test-repository",
            HostPath = Encoding.UTF8.GetBytes(_root),
            Mode = SelectedFolderMode.Copy,
            CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        _ = await dbContext.SaveChangesAsync().ConfigureAwait(false);
        return provider;
    }

    public static DevelopmentCreateProjectCommand CreateSeed() =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "objective",
            SelectedFolderId,
            "repository-hash",
            "main",
            "task",
            "requirements",
            "[\"acceptance\"]");

    /// <summary>
    ///     A project whose single task the review chain has taken to <c>AwaitingApply</c> — the status a Dev Mode task
    ///     reaches when its review approved it and only the apply is left. Shared, because the dev-workflows suite needs
    ///     the same real task to hang a <c>DevTask</c> node run's <c>DevelopmentTaskId</c> off.
    /// </summary>
    public static async Task<(DevelopmentCreateProjectCommand Seed, long Version)> SeedTaskAwaitingApplyAsync(IDevelopmentStore store)
    {
        var seed = CreateSeed();
        _ = await store.CreateProjectAsync(seed).ConfigureAwait(false);
        var version = 1L;
        foreach (var status in new[]
                 {
                     DevelopmentTaskStatus.Ready,
                     DevelopmentTaskStatus.InProgress,
                     DevelopmentTaskStatus.Validation,
                     DevelopmentTaskStatus.InReview,
                     DevelopmentTaskStatus.AwaitingApply
                 })
        {
            var result = await store.TransitionTaskAsync(new DevelopmentTransitionTaskCommand(seed.TaskId,
                                        Guid.NewGuid(),
                                        status,
                                        version,
                                        ApprovedSubjectHash: status == DevelopmentTaskStatus.AwaitingApply ? "subject" : null))
                                    .ConfigureAwait(false);
            version = result.Version;
        }

        return (seed, version);
    }

    public void Dispose()
    {
        _keyHolder.Dispose();

        // Windows refuses to remove a directory that still contains an open file, and Microsoft.Data.Sqlite's
        // connection pool holds the .sqlite handle open after the DbContext is disposed. Linux unlinks it regardless,
        // which is why this teardown never failed there.
        SqliteFileProbe.ReleasePooledHandles();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

internal sealed class TestDataDirectory(string root) : INodeDataDirectory
{
    public string Root { get; } = root;
}

internal class TestApplyPort : IDevelopmentHostApplyPort
{
    public virtual Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);

    public virtual Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class CrashAfterMutationApplyPort : TestApplyPort
{
    private bool _applied;

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_applied ? DevelopmentHostApplyState.ExactApprovedResultPresent : DevelopmentHostApplyState.UnappliedBaseUnchanged);

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        _applied = true;
        throw new InvalidOperationException("Simulated crash after host mutation.");
    }
}

internal sealed class CrashBeforeHostMutationApplyPort : TestApplyPort
{
    private bool _crashed;

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        if (!_crashed)
        {
            _crashed = true;
            throw new InvalidOperationException("Simulated crash after ApplyStarted and before host mutation.");
        }

        return Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class CountingApplyPort : TestApplyPort
{
    public int InspectCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        return Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}

internal sealed class AmbiguousApplyPort : TestApplyPort
{
    public int InspectCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        InspectCalls++;
        return Task.FromResult(DevelopmentHostApplyState.Ambiguous);
    }

    public override Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls++;
        return Task.CompletedTask;
    }
}
