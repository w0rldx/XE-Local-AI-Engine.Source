namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client;
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
                ConfigureBuilder = static builder =>
                {
                    builder.WebHost.UseTestServer();

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
