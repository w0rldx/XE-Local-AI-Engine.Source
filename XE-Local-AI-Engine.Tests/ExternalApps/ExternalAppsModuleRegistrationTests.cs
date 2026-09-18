namespace XE_Local_AI_Engine.Tests.ExternalApps;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The External Apps module against a REAL container assembled by the production module extension, rather than
///     by a hand-rolled service collection.
///     <para>
///         It exists because nothing else would catch a mis-wired module. Every other suite in this directory
///         constructs the service, the gates and the two hosted services by hand, so a registration that was never
///         added — or added as the wrong lifetime — would pass all of them and fail only on a running node.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ExternalAppsModuleRegistrationTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Container_ResolvesEveryPublicService_WhateverTheFeatureFlagSays(bool enabled)
    {
        await using var host = BuildHost(enabled);

        AssertEx.Equal(enabled, host.Services.GetRequiredService<IOptions<ExternalAppsOptions>>().Value.Enabled);
        AssertEx.NotNull(host.Services.GetRequiredService<IExternalAppService>());
        AssertEx.NotNull(host.Services.GetRequiredService<IExternalAppStartupReconciler>());
        AssertEx.NotNull(host.Services.GetRequiredService<IExternalAppEventPublisher>());

        // Scoped, like every other store: resolving it from the root would be a database context living as long as
        // the node.
        await using var scope = host.Services.CreateAsyncScope();
        AssertEx.NotNull(scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>());
    }

    /// <summary>
    ///     The gate and the runner ARE the per-instance mutual exclusion and the in-flight operation map. Two copies
    ///     of either would let two commands hold one instance, and would leave a cancel unable to see the operation
    ///     it is meant to stop.
    /// </summary>
    [Test]
    public async Task Container_ResolvesOneGateOneRunnerAndOneService()
    {
        await using var host = BuildHost(enabled: true);

        AssertEx.True(ReferenceEquals(host.Services.GetRequiredService<ExternalAppInstanceGate>(),
                host.Services.GetRequiredService<ExternalAppInstanceGate>()),
            "Two instance gates would let two commands hold one instance at once.");
        AssertEx.True(ReferenceEquals(host.Services.GetRequiredService<ExternalAppOperationRunner>(),
                host.Services.GetRequiredService<ExternalAppOperationRunner>()),
            "Two operation runners would leave a cancel unable to see the operation it is meant to stop.");

        // The service, the reconciler's dependency and the interface every endpoint takes must all be one object.
        AssertEx.True(ReferenceEquals(host.Services.GetRequiredService<IExternalAppService>(),
                host.Services.GetRequiredService<ExternalAppService>()),
            "The interface and the concrete service resolved to different objects, so the hosted services and the API would hold different state.");
    }

    /// <summary>
    ///     The refresh endpoint re-runs the boot pass through the interface. If that resolved to a second object,
    ///     the refresh would be a different reconciler from the one that ran at startup.
    /// </summary>
    [Test]
    public async Task Container_ResolvesTheReconcilerAsOneObjectInBothItsRoles()
    {
        await using var host = BuildHost(enabled: true);

        var byInterface = host.Services.GetRequiredService<IExternalAppStartupReconciler>();
        var hosted = host.Services.GetServices<IHostedService>().OfType<ExternalAppStartupReconciler>().ToList();

        AssertEx.Equal(expected: 1, hosted.Count, "The reconciler must be hosted exactly once.");
        AssertEx.True(ReferenceEquals(byInterface, hosted[0]), "The hosted reconciler and the refresh endpoint's reconciler must be one object.");
    }

    [Test]
    public async Task Container_HostsBothTheReconcilerAndTheObserver()
    {
        await using var host = BuildHost(enabled: true);
        var hosted = host.Services.GetServices<IHostedService>().ToList();

        AssertEx.Contains(hosted, static service => service is ExternalAppStartupReconciler);
        AssertEx.Contains(hosted, static service => service is ExternalAppStateObserver);
    }

    /// <summary>
    ///     Registration is not the feature flag, so both hosted services exist while it is off — and both must do
    ///     nothing at all rather than reach for a daemon that may not be installed.
    /// </summary>
    [Test]
    public async Task Container_WhenTheFeatureIsDisabled_StartsBothHostedServicesWithoutTouchingAnything()
    {
        await using var host = BuildHost(enabled: false);
        var hosted = host.Services.GetServices<IHostedService>()
                         .Where(static service => service is ExternalAppStartupReconciler or ExternalAppStateObserver)
                         .ToList();

        AssertEx.Equal(expected: 2, hosted.Count);

        foreach (var service in hosted)
        {
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }

        AssertEx.Equal(ExternalAppReconcileSummary.Nothing,
            await host.Services.GetRequiredService<IExternalAppStartupReconciler>().ReconcileAsync());
    }

    /// <summary>
    ///     The two free-form options the annotations cannot express are validated at startup, so an operator learns
    ///     about a misspelt instance root from the node failing to start rather than from their first install
    ///     failing with a storage error.
    /// </summary>
    [Test]
    public async Task Container_WithARelativeInstanceRoot_FailsOptionsValidation()
    {
        await using var host = BuildHost(enabled: true, instanceRoot: "not-absolute");

        var exception = await AssertEx.ThrowsAsync<OptionsValidationException>(() =>
        {
            _ = host.Services.GetRequiredService<IOptions<ExternalAppsOptions>>().Value;
            return Task.CompletedTask;
        });

        AssertEx.Contains(string.Join(" ", exception.Failures), "not-absolute");
    }

    private static TestHost BuildHost(bool enabled, string? instanceRoot = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ExternalApps:Enabled"] = enabled ? "true" : "false"
        };

        if (instanceRoot is not null)
        {
            values["ExternalApps:InstanceRoot"] = instanceRoot;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });

        // ValidateOnBuild off, ValidateScopes on: whole-graph validation would walk registrations from modules this
        // test deliberately does not compose, which is a different test's job and needs the whole host.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        }));
        builder.Configuration.AddInMemoryCollection(values);

        var root = Path.Combine(Path.GetTempPath(), "xe-ext-apps-registration-" + Guid.NewGuid().ToString("N"));

        // Supplied by other AddNode* modules in the real host. Constructing the layout only reads the path; nothing
        // here creates a directory or opens the database.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(root));
        builder.Services.TryAddSingleton(Substitute.For<IRuntimeDeviceAudit>());
        builder.Services.TryAddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        builder.Services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "node.sqlite")}"));

        builder.AddNodeContainerSandbox(builder.Configuration);
        builder.AddNodeContainerRuntime(builder.Configuration);
        builder.AddNodeExternalAppsCatalog(builder.Configuration);
        builder.AddNodeExternalApps(builder.Configuration);

        return new TestHost(builder.Build());
    }

    /// <summary>
    ///     Async-disposing wrapper. <see cref="IHost" /> declares only <see cref="IDisposable" />, and synchronous
    ///     disposal of a container holding an <see cref="IAsyncDisposable" />-only singleton throws rather than
    ///     disposing it.
    /// </summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private readonly IHost _host;

        public TestHost(IHost host)
        {
            _host = host;
        }

        public IServiceProvider Services => _host.Services;

        public ValueTask DisposeAsync()
        {
            return ((IAsyncDisposable)_host).DisposeAsync();
        }
    }
}
