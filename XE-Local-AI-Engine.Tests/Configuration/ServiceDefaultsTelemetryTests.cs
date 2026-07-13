namespace XE_Local_AI_Engine.Tests.Configuration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

// Composition contract for AddServiceDefaults (ServiceDefaults/Extensions.cs), MED-005:
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
        // The MED-005 fix: desktop hosting with the standard OTLP variable set now actually exports.
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
}
