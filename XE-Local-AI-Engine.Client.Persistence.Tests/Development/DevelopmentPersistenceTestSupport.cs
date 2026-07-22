namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
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

    public static DevelopmentCreateProjectCommand CreateSeed()
        => new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "objective",
            SelectedFolderId,
            "repository-hash",
            "main",
            "task",
            "requirements",
            "[\"acceptance\"]");

    public void Dispose()
    {
        _keyHolder.Dispose();
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
        CancellationToken cancellationToken = default)
        => Task.FromResult(DevelopmentHostApplyState.UnappliedBaseUnchanged);

    public virtual Task ApplyAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class CrashAfterMutationApplyPort : TestApplyPort
{
    private bool _applied;

    public int ApplyCalls { get; private set; }

    public override Task<DevelopmentHostApplyState> InspectAsync(DevelopmentApprovedApplySubject subject,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_applied ? DevelopmentHostApplyState.ExactApprovedResultPresent : DevelopmentHostApplyState.UnappliedBaseUnchanged);

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
