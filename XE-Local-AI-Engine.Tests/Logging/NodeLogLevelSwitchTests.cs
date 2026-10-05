namespace XE_Local_AI_Engine.Tests.Logging;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The verbose switch on the real <c>AddServices</c> Serilog pipeline: it must be applied after
///     <c>ReadFrom.Configuration</c> (or the configured Default wins) and must leave the Microsoft overrides in force.
/// </summary>
// AddServices mutates the process-global FastEndpoints serializer options outside the host factory's startup lock.
[NotInParallel]
[Category(TestCategories.Integration)]
public sealed class NodeLogLevelSwitchTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-level-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public void Verbose_WidensTheNodeNamespaces_ButNotTheMicrosoftOverrides()
    {
        var sink = new CapturingSink();
        var builder = CreateBuilder();
        builder.Logging.ClearProviders();
        builder.AddServices(builder.Configuration, NodeStartupSettings.Read(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton<ILogEventSink>(sink);
        using var provider = builder.Services.BuildServiceProvider();
        var factory = provider.GetRequiredService<ILoggerFactory>();
        var node = factory.CreateLogger("XE_Local_AI_Engine.Client.Probe");
        var aspNet = factory.CreateLogger("Microsoft.AspNetCore.Probe");
        var levelSwitch = provider.GetRequiredService<NodeLogLevelSwitch>();

        node.LogDebug("before");
        levelSwitch.Set(true);
        node.LogDebug("verbose-node");
        aspNet.LogDebug("verbose-aspnet");
        levelSwitch.Set(false);
        node.LogDebug("after");

        AssertEx.True(levelSwitch.Level.MinimumLevel == LogEventLevel.Information, "Set(false) restores the configured level.");
        AssertEx.Equal("verbose-node", string.Join(" | ", sink.Messages()));
    }

    [Test]
    [Arguments("Warning", false, LogEventLevel.Warning)]
    [Arguments("Debug", true, LogEventLevel.Debug)]
    [Arguments(null, false, LogEventLevel.Information)]
    [Arguments("nonsense", false, LogEventLevel.Information)]
    public void Set_False_RestoresTheConfiguredLevel(string? configured, bool verbose, LogEventLevel level)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Serilog:MinimumLevel:Default"] = configured
                            })
                            .Build();
        var levelSwitch = new NodeLogLevelSwitch(configuration);

        levelSwitch.Set(true);
        AssertEx.True(levelSwitch.Verbose);
        levelSwitch.Set(false);

        AssertEx.Equal(verbose, levelSwitch.Verbose);
        AssertEx.Equal(level, levelSwitch.Level.MinimumLevel);
    }

    private WebApplicationBuilder CreateBuilder()
    {
        Directory.CreateDirectory(_rootPath);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Directory.GetCurrentDirectory()
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:LocalChat:DefaultModel"] = "llama3.2",
            ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(_rootPath, "switch.sqlite")}",
            ["Ollama:Endpoint"] = "http://127.0.0.1:11434",
            ["Serilog:MinimumLevel:Default"] = "Information",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Warning"
        });

        return builder;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public void Emit(LogEvent logEvent) =>
            _events.Enqueue(logEvent);

        public string[] Messages() =>
            _events.Where(static e => e.Properties.TryGetValue("SourceContext", out var source) && source.ToString().Contains("Probe", StringComparison.Ordinal))
                   .Select(static e => e.RenderMessage(CultureInfo.InvariantCulture))
                   .ToArray();
    }
}
