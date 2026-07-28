namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Tests.Testing;

// Regression guard for the OTLP log-export path.
//
// ConfigureServices.AddServices registers Serilog. Serilog's writeToProviders parameter defaults to FALSE, which makes
// Serilog the terminus of the logging pipeline: events reach Serilog's own sinks and no other registered
// ILoggerProvider. The OpenTelemetry logger provider (ServiceDefaults/Extensions.cs ConfigureOpenTelemetry) is one of
// those, so with the default every ILogger call dead-ends before the OTLP log exporter — the Aspire dashboard shows
// zero structured logs while traces and metrics still arrive, because those bypass ILoggerFactory entirely.
//
// This asserts the observable consequence (a second provider receives events) rather than the flag itself, so it keeps
// holding if the registration is restructured.
public sealed class SerilogProviderForwardingTests : IDisposable
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
    public void AddServices_ForwardsLogEventsToOtherProviders_SoOpenTelemetryCanExportThem()
    {
        var recorded = new List<string>();

        var builder = CreateBuilder();
        // Mirrors Program.cs, which clears providers before AddServiceDefaults/AddServices compose the real pipeline.
        builder.Logging.ClearProviders();
        builder.AddServices(builder.Configuration);
        // Stands in for the OpenTelemetry logger provider: any provider other than Serilog's own sinks.
        using var recordingProvider = new RecordingLoggerProvider(recorded);
        builder.Logging.AddProvider(recordingProvider);

        using var provider = builder.Services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("otel.forwarding.probe");

        logger.LogWarning("otel-forwarding-probe-message");

        AssertEx.True(recorded.Any(message => message.Contains("otel-forwarding-probe-message", StringComparison.Ordinal)),
            "Serilog must forward log events to other ILoggerProviders (AddSerilog writeToProviders: true); otherwise the "
            + "OpenTelemetry logger provider never sees them and OTLP log export is silently dead.");
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
            ["CentralPlatform:BaseUrl"] = "https://127.0.0.1",
            ["ConnectionStrings:node-sqlite"] = $"Data Source={Path.Combine(_rootPath, "forwarding.sqlite")}",
            ["Ollama:Endpoint"] = "http://127.0.0.1:11434"
        });

        return builder;
    }

    private sealed class RecordingLoggerProvider(List<string> recorded) : ILoggerProvider
    {
        private readonly List<string> _recorded = recorded;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(_recorded);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger(List<string> recorded) : ILogger
    {
        private readonly List<string> _recorded = recorded;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_recorded)
            {
                _recorded.Add(formatter(state, exception));
            }
        }
    }
}
