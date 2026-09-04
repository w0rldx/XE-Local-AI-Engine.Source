namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Builds the real composition root with <see cref="ServiceProviderOptions.ValidateScopes" /> and
///     <see cref="ServiceProviderOptions.ValidateOnBuild" /> forced on, and resolves every registered
///     <see cref="IHostedService" />.
/// </summary>
/// <remarks>
///     <para>
///         The product only turns both flags on in the Development environment
///         (<c>Program.CreateAppAsync</c> → <c>UseDefaultServiceProvider</c>), and every other suite runs the host as
///         <c>Testing</c> — so a captive dependency or a missing registration reaches an operator's Release build
///         without any test having looked. This class is the explicit assertion that closes that gap.
///     </para>
///     <para>
///         Hosted services are resolved but never started: <c>CreateAppAsync</c> returns a built-but-not-started host,
///         and construction alone is what proves each registration's graph is satisfiable — including the
///         factory-lambda registrations that <c>ValidateOnBuild</c> cannot see into. Unlike every other integration
///         class here, this one keeps the product's hosted services instead of removing them, which is the whole point.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class ServiceProviderValidationTests
{
    [Test]
    public async Task CompositionRoot_BuildsWithScopeAndBuildValidationEnabled()
    {
        await using var host = await ValidatedHost.CreateAsync();

        // Reaching here means Build() itself passed ValidateOnBuild: every non-factory registration's dependencies are
        // registered, and no singleton captures a scoped service.
        AssertEx.NotNull(host.App.Services);
    }

    /// <summary>
    ///     The reasoning-effort dispatcher must be resolvable ONLY from a scope. Its own dependencies are scoped, and
    ///     the invocation runner that uses it is a singleton, so a captive dependency here would be silent in
    ///     production and fatal under load. <c>CompositionRoot_BuildsWithScopeAndBuildValidationEnabled</c> above is
    ///     the negative half — the build itself fails if the runner ever captures it — and this is the positive half.
    /// </summary>
    [Test]
    public async Task ReasoningEffortDispatcher_ResolvesFromAScopeOnly()
    {
        await using var host = await ValidatedHost.CreateAsync();

        AssertEx.Throws<InvalidOperationException>(() => host.App.Services.GetRequiredService<IReasoningEffortDispatcher>());

        using var scope = host.App.Services.CreateScope();
        AssertEx.NotNull(scope.ServiceProvider.GetRequiredService<IReasoningEffortDispatcher>());
    }

    [Test]
    public async Task EveryRegisteredHostedService_Resolves()
    {
        await using var host = await ValidatedHost.CreateAsync();

        var hostedServices = host.App.Services.GetServices<IHostedService>().ToList();

        AssertEx.NotEmpty(hostedServices);
        foreach (var hostedService in hostedServices)
        {
            AssertEx.NotNull(hostedService);
        }
    }

    [Test]
    public async Task HostCreation_LeavesTheContentRootNodeSettingsWhereItIs()
    {
        // NodeDataDirectory's first-launch migration File.Moves node-settings.json (and the encrypted credential
        // stores) out of the content root whenever the data dir differs. This class's content root is the REAL Client
        // source directory, so an unisolated INodeDataDirectory eats the developer's dev-node settings and deletes them
        // with the temp data dir on teardown. The registration must stay isolated.
        var contentRoot = TestServerWebAppFactory.ResolveClientContentRoot();
        var canaryPath = Path.Combine(contentRoot, "node-settings.json");

        // Only ever remove a file this test created — a real dev node's settings must survive untouched.
        var wroteCanary = !File.Exists(canaryPath);
        if (wroteCanary)
        {
            await File.WriteAllTextAsync(canaryPath, "{}");
        }

        try
        {
            await using (var host = await ValidatedHost.CreateAsync())
            {
                AssertEx.NotEqual(contentRoot, host.App.Services.GetRequiredService<INodeDataDirectory>().Root);
            }

            AssertEx.True(File.Exists(canaryPath), "Building the validated host moved node-settings.json out of the Client content root.");
        }
        finally
        {
            if (wroteCanary)
            {
                File.Delete(canaryPath);
            }
        }
    }

    /// <summary>A throwaway host rooted in temp directories, mirroring the fixture's configuration block.</summary>
    private sealed class ValidatedHost : IAsyncDisposable
    {
        private readonly string _nodeDataDirectory;
        private readonly string _sqlitePath;
        private readonly string _webRoot;

        private ValidatedHost(WebApplication app, string webRoot, string nodeDataDirectory, string sqlitePath)
        {
            App = app;
            _webRoot = webRoot;
            _nodeDataDirectory = nodeDataDirectory;
            _sqlitePath = sqlitePath;
        }

        public WebApplication App { get; }

        public static async Task<ValidatedHost> CreateAsync()
        {
            var webRoot = Path.Combine(Path.GetTempPath(), $"xe-di-validation-wwwroot-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(webRoot);
            await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><html lang=\"en\"><body></body></html>");
            var nodeDataDirectory = Path.Combine(Path.GetTempPath(), $"xe-di-validation-nodedata-{Guid.NewGuid():N}");
            _ = Directory.CreateDirectory(nodeDataDirectory);
            var sqlitePath = Path.Combine(Path.GetTempPath(), $"xe-di-validation-{Guid.NewGuid():N}.sqlite");

            var start = await Program.CreateAppAsync([], new ProgramAppCustomization
            {
                EnvironmentName = "Testing",
                ContentRootPath = TestServerWebAppFactory.ResolveClientContentRoot(),
                WebRootPath = webRoot,
                Configuration = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:node-sqlite"] = $"Data Source={sqlitePath}",
                    ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
                    ["XE_USE_LOCAL_MODEL_PROVIDER"] = "true",
                    ["NodeData:Directory"] = nodeDataDirectory,
                    ["EntityFramework:ServiceProviderCaching"] = "false"
                },
                ConfigureBuilder = builder =>
                {
                    builder.WebHost.UseTestServer();

                    // The content root above is the REAL Client source directory while the data dir is a temp GUID dir,
                    // so the product NodeDataDirectory would run its first-launch migration and File.Move a developer's
                    // node-settings.json (and the encrypted credential stores) out of the checkout into that temp dir —
                    // which teardown then deletes. ValidateOnBuild constructs every singleton, so it happens on every
                    // run of this class. The fake pins the same Root with no migration.
                    builder.Services.RemoveAll<INodeDataDirectory>();
                    builder.Services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(nodeDataDirectory));

                    // Runs after every product registration and after the product's own environment-conditional call,
                    // so this is what the container is actually built with.
                    _ = builder.Host.UseDefaultServiceProvider(static options =>
                    {
                        options.ValidateScopes = true;
                        options.ValidateOnBuild = true;
                    });
                }
            });

            var app = start.App ?? throw new InvalidOperationException($"CreateAppAsync early-exited with code {start.ExitCode}.");
            return new ValidatedHost(app, webRoot, nodeDataDirectory, sqlitePath);
        }

        public async ValueTask DisposeAsync()
        {
            // The host was never started, so there is nothing to stop.
            await App.DisposeAsync();
            SqliteConnection.ClearAllPools();
            TryDelete(_webRoot);
            TryDelete(_nodeDataDirectory);
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_sqlitePath)!, Path.GetFileName(_sqlitePath) + "*"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Best-effort temp cleanup.
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
