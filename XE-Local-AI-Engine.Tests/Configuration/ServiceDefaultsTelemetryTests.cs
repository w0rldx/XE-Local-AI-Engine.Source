namespace XE_Local_AI_Engine.Tests.Configuration;

using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using XE_Local_AI_Engine.Tests.Testing;

// Composition contract for AddServiceDefaults (ServiceDefaults/Extensions.cs):
//  - OpenTelemetry instrumentation is registered in EVERY hosting mode (not only under Aspire).
//  - The OTLP exporter is registered whenever OTEL_EXPORTER_OTLP_ENDPOINT is configured, regardless of ASPIRE_ENABLED
//    — so a desktop/RC build with the standard variable set now exports.
//  - Aspire-specific service discovery (and the global HTTP resilience/discovery defaults, which live in the same
//    conditional block) stays gated on ASPIRE_ENABLED.
// The four tests below cover ASPIRE_ENABLED {off,on} x OTLP endpoint {unset,set}.
public sealed class ServiceDefaultsTelemetryTests
{
    // AddOpenTelemetry() registers its hosted service from this assembly; a descriptor from it means instrumentation
    // was wired regardless of exporter presence.
    private const string OpenTelemetryHostingAssembly = "OpenTelemetry.Extensions.Hosting";

    // UseOtlpExporter() registers types (including UseOtlpExporterRegistration.Instance) from this assembly; a
    // descriptor from it is a stable signal that the OTLP exporter was added.
    private const string OtlpExporterAssembly = "OpenTelemetry.Exporter.OpenTelemetryProtocol";

    // AddServiceDiscovery() registers types from this assembly; used here as the observable proxy for the whole
    // ASPIRE_ENABLED-gated block (service discovery + the global ConfigureHttpClientDefaults resilience/discovery).
    private const string ServiceDiscoveryAssembly = "Microsoft.Extensions.ServiceDiscovery";

    // Must match the Meter name in XE_Local_AI_Engine.AI.Agent.Chat.ProviderCallBudgetChatClient. This name is added to
    // the WithMetrics AddMeter list so the agent's provider-round / budget counters are actually exported.
    private const string AgentBudgetMeterName = "XE.LocalAiEngine.AI.Agent";

    [Test]
    public void ConfigureOpenTelemetry_RegistersAgentBudgetMeter_SoItsInstrumentsAreExported()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "ServiceDefaultsTelemetryTests",
            EnvironmentName = "Development"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>());

        // The exporter (owned by the reader, disposed with the MeterProvider on host disposal) writes exported meter
        // names into this closure list so the assertion does not have to hold the exporter alive itself.
        var exportedMeterNames = new List<string>();
        builder.AddServiceDefaults();
        // Compose an in-test reader onto the same MeterProvider AddServiceDefaults configured (WithMetrics is additive),
        // so what gets exported reflects the production AddMeter set — not a reader we configured in isolation.
        builder.Services.AddOpenTelemetry()
               .WithMetrics(metrics => metrics.AddReader(new BaseExportingMetricReader(new RecordingMetricExporter(exportedMeterNames))));

        using var host = builder.Build();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        // A counter on a Meter whose NAME matches the agent budget meter. OpenTelemetry collects an instrument only when
        // its meter name was registered via AddMeter, so this measurement is exported iff that registration is present.
        using var meter = new Meter(AgentBudgetMeterName);
        meter.CreateCounter<long>("xe.agent.provider_rounds.export_probe").Add(1);

        meterProvider.ForceFlush();

        AssertEx.True(exportedMeterNames.Contains(AgentBudgetMeterName),
            $"The '{AgentBudgetMeterName}' meter must be exported; ConfigureOpenTelemetry must AddMeter it (MED-007).");
    }

    [Test]
    public void DesktopMode_NoOtlpEndpoint_RegistersInstrumentationButNoExporterOrServiceDiscovery()
    {
        var services = BuildServiceDefaults(aspireEnabled: false, otlpEndpointConfigured: false);

        AssertEx.True(HasServiceFromAssembly(services, OpenTelemetryHostingAssembly),
            "OpenTelemetry instrumentation must be registered in desktop mode.");
        AssertEx.False(HasServiceFromAssembly(services, OtlpExporterAssembly),
            "No OTLP exporter should be registered when no endpoint is configured.");
        AssertEx.False(HasServiceFromAssembly(services, ServiceDiscoveryAssembly),
            "Service discovery is Aspire-specific and must not be registered in desktop mode.");
    }

    [Test]
    public void DesktopMode_WithOtlpEndpoint_RegistersInstrumentationAndExporterWithoutServiceDiscovery()
    {
        // Desktop hosting with the standard OTLP variable set now actually exports.
        var services = BuildServiceDefaults(aspireEnabled: false, otlpEndpointConfigured: true);

        AssertEx.True(HasServiceFromAssembly(services, OpenTelemetryHostingAssembly),
            "OpenTelemetry instrumentation must be registered in desktop mode.");
        AssertEx.True(HasServiceFromAssembly(services, OtlpExporterAssembly),
            "The OTLP exporter must be registered in desktop mode when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
        AssertEx.False(HasServiceFromAssembly(services, ServiceDiscoveryAssembly),
            "Service discovery is Aspire-specific and must not be registered outside Aspire.");
    }

    [Test]
    public void AspireMode_NoOtlpEndpoint_RegistersInstrumentationAndServiceDiscoveryWithoutExporter()
    {
        var services = BuildServiceDefaults(aspireEnabled: true, otlpEndpointConfigured: false);

        AssertEx.True(HasServiceFromAssembly(services, OpenTelemetryHostingAssembly),
            "OpenTelemetry instrumentation must be registered under Aspire.");
        AssertEx.False(HasServiceFromAssembly(services, OtlpExporterAssembly),
            "No OTLP exporter should be registered when no endpoint is configured, even under Aspire.");
        AssertEx.True(HasServiceFromAssembly(services, ServiceDiscoveryAssembly),
            "Service discovery must be registered under Aspire.");
    }

    [Test]
    public void AspireMode_WithOtlpEndpoint_RegistersInstrumentationExporterAndServiceDiscovery()
    {
        var services = BuildServiceDefaults(aspireEnabled: true, otlpEndpointConfigured: true);

        AssertEx.True(HasServiceFromAssembly(services, OpenTelemetryHostingAssembly),
            "OpenTelemetry instrumentation must be registered under Aspire.");
        AssertEx.True(HasServiceFromAssembly(services, OtlpExporterAssembly),
            "The OTLP exporter must be registered when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
        AssertEx.True(HasServiceFromAssembly(services, ServiceDiscoveryAssembly),
            "Service discovery must be registered under Aspire.");
    }

    private static IServiceCollection BuildServiceDefaults(bool aspireEnabled, bool otlpEndpointConfigured)
    {
        // An empty builder isolates the test from ambient environment variables (ASPIRE_ENABLED / OTEL_*) that could
        // otherwise be present on the host; the two flags under test are supplied purely via in-memory configuration.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "ServiceDefaultsTelemetryTests",
            EnvironmentName = "Development"
        });

        var settings = new Dictionary<string, string?>();
        if (aspireEnabled)
        {
            settings["ASPIRE_ENABLED"] = "true";
        }

        if (otlpEndpointConfigured)
        {
            settings["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317";
        }

        builder.Configuration.AddInMemoryCollection(settings);

        builder.AddServiceDefaults();

        return builder.Services;
    }

    private static bool HasServiceFromAssembly(IServiceCollection services, string assemblyName)
    {
        return services.Any(descriptor =>
            MatchesAssembly(descriptor.ServiceType, assemblyName)
            || MatchesAssembly(descriptor.ImplementationType, assemblyName)
            || MatchesAssembly(descriptor.ImplementationInstance?.GetType(), assemblyName));
    }

    private static bool MatchesAssembly(Type? type, string assemblyName)
    {
        return string.Equals(type?.Assembly.GetName().Name, assemblyName, StringComparison.Ordinal);
    }

    // Records the meter name of every exported metric into a caller-owned list so a test can assert a given meter's
    // instruments flowed through the configured MeterProvider (i.e. were registered via AddMeter).
    private sealed class RecordingMetricExporter(List<string> exportedMeterNames) : BaseExporter<Metric>
    {
        private readonly List<string> _exportedMeterNames = exportedMeterNames;

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                _exportedMeterNames.Add(metric.MeterName);
            }

            return ExportResult.Success;
        }
    }
}
