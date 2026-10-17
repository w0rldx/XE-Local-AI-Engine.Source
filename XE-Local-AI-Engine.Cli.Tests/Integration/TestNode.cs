namespace XE_Local_AI_Engine.Cli.Tests.Integration;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Cli.Api;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     A real node on TestServer for the CLI's integration tests: the product composition built by
///     <c>Program.CreateAppAsync</c>, with temp data, no hosted services and the overrides a test passes.
/// </summary>
/// <remarks>
///     The CLI tests cannot reference <c>XE-Local-AI-Engine.Tests</c>, whose <c>TestServerWebAppFactory</c> also starts a
///     FakeOllama server; these tests replace the chat stream service instead, so they keep only the host deviations
///     that matter here. Host bootstrap mutates process-global state, so starts are serialized.
/// </remarks>
internal sealed class TestNode : IAsyncDisposable
{
    internal const string AdminEmail = "admin@example.test";
    internal const string AdminPassword = "Str0ng!Password123";

    private static readonly SemaphoreSlim StartupLock = new(initialCount: 1, maxCount: 1);

    private readonly string _root;
    private readonly WebApplication _app;

    private TestNode(string root, WebApplication app)
    {
        _root = root;
        _app = app;
    }

    public TestServer Server => _app.GetTestServer();

    public NodeConnectionOptions ConnectionOptions =>
        new()
        {
            Origin = new Uri("http://localhost/"),
            HandlerFactory = Server.CreateHandler
        };

    public static async Task<TestNode> StartAsync(Action<IServiceCollection>? configureServices = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"xe-cli-node-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(root, "data");
        var webRoot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(webRoot);
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>XE</title>");
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(root, "node.sqlite")}",
            ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
            ["NodeData:Directory"] = dataDirectory,
            ["EntityFramework:ServiceProviderCaching"] = "false"
        };

        await StartupLock.WaitAsync();
        try
        {
            var start = await Program.CreateAppAsync([], new ProgramAppCustomization
            {
                EnvironmentName = "Testing",
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = webRoot,
                Configuration = configuration,
                ConfigureBuilder = builder =>
                {
                    builder.WebHost.UseTestServer();
                    builder.Services.RemoveAll<IHostedService>();
                    builder.Services.RemoveAll<INodeDataDirectory>();
                    builder.Services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(dataDirectory));
                    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "dp-keys")));
                    configureServices?.Invoke(builder.Services);
                }
            });
            var app = start.App ?? throw new InvalidOperationException($"CreateAppAsync early-exited with code {start.ExitCode}.");
            await app.StartAsync();
            return new TestNode(root, app);
        }
        finally
        {
            StartupLock.Release();
        }
    }

    /// <summary>Completes first-run setup with <see cref="AdminEmail" /> and <see cref="AdminPassword" />, as the SPA would.</summary>
    public async Task SetUpAdminAsync()
    {
        using var client = Server.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/local/v1/auth/setup", new
        {
            email = AdminEmail,
            password = AdminPassword
        });
        _ = response.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a racing file is not a test failure.
            }
        }
    }
}
