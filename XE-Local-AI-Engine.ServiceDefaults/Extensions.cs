namespace Microsoft.Extensions.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
/// <summary>
///     Represents extensions.
/// </summary>
public static class Extensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults()
        {
            // OpenTelemetry instrumentation is registered unconditionally so telemetry works in every hosting mode,
            // not only under Aspire. In desktop/RC hosting an operator can set the standard OTEL_EXPORTER_OTLP_ENDPOINT
            // variable and have traces/metrics/logs exported; with no endpoint configured the SDK records in-process
            // only and attaches no exporter or export loop, so it stays lean (see AddOpenTelemetryExporters below).
            builder.ConfigureOpenTelemetry();

            // Service discovery and the global HTTP resilience/discovery defaults are Aspire-specific: they resolve the
            // "scheme://service-name" addresses the AppHost injects, which only exist inside an Aspire-orchestrated run.
            // They stay gated on ASPIRE_ENABLED (set by the AppHost via WithEnvironment). Read from configuration rather
            // than the raw environment so the flag is unit-testable; configuration includes environment variables, so
            // real Aspire runs are unaffected.
            var aspireEnabled = string.Equals(builder.Configuration["ASPIRE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);

            if (aspireEnabled)
            {
                builder.Services.AddServiceDiscovery();

                builder.Services.ConfigureHttpClientDefaults(http =>
                {
                    // Turn on resilience by default
                    http.AddStandardResilienceHandler();

                    // Turn on service discovery by default
                    http.AddServiceDiscovery();
                });
            }

            return builder;
        }

        public TBuilder ConfigureOpenTelemetry()
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                   .WithMetrics(metrics =>
                   {
                       metrics.AddAspNetCoreInstrumentation()
                              .AddHttpClientInstrumentation()
                              .AddRuntimeInstrumentation()
                              // Literal must match XE_Local_AI_Engine.Client.Common.Telemetry.NodeMetrics.MeterName
                              // ("XE.Node"); ServiceDefaults cannot reference the Client project.
                              .AddMeter("XE.Node")
                              // Literal must match the Meter name used by the ProviderCallBudgetChatClient in the
                              // AI.Agent project, which is XE.LocalAiEngine.AI.Agent; ServiceDefaults cannot reference
                              // that project. Mirrors the identically named tracing AddSource below so the agent's
                              // provider-round and budget counters are exported, not just recorded in-process. MED-007.
                              .AddMeter("XE.LocalAiEngine.AI.Agent")
                              // Flows the OpenTelemetryChatClient's gen_ai token/duration metrics (MEAI meter
                              // "Microsoft.Extensions.AI"); wildcard mirrors the tracing AddSource below.
                              .AddMeter("Microsoft.Extensions.AI*");
                   })
                   .WithTracing(tracing =>
                   {
                       tracing.AddSource(builder.Environment.ApplicationName)
                              // Literal must match XE_Local_AI_Engine.Client.Common.Telemetry.NodeActivitySource.SourceName
                              // ("XE.Node"); ServiceDefaults cannot reference the Client project. Exports the coarse
                              // pre-spawn turn/readiness spans (AUD4-23), mirroring the identically named AddMeter above.
                              .AddSource("XE.Node")
                              .AddSource("XE.LocalAiEngine.AI.Agent")
                              .AddSource("Microsoft.Agents.AI*")
                              .AddSource("Microsoft.Extensions.AI*")
                              .AddAspNetCoreInstrumentation(tracing =>
                                  tracing.Filter = context =>
                                      !context.Request.Path.StartsWithSegments("/health/live", StringComparison.CurrentCulture))
                              .AddHttpClientInstrumentation()
                              // Downgrade a gen_ai span that failed only because a user pressed Stop (Error→Unset) so a
                              // cancelled turn doesn't read as a service fault on dashboards/alerts (GPTAUD-19a).
                              .AddProcessor(new GenAiCancellationStatusProcessor());
                   });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        // The OTLP exporter is added whenever OTEL_EXPORTER_OTLP_ENDPOINT is configured — the standard OpenTelemetry
        // variable — regardless of hosting mode. In Aspire-orchestrated runs the AppHost auto-injects that variable
        // (standard AddProject behavior), so the meters/sources wired above (XE.Node, Microsoft.Agents.AI*,
        // Microsoft.Extensions.AI*) flow to the Aspire dashboard with no extra config. In desktop mode an operator who
        // points the variable at a collector gets the same export path; when it is unset (the desktop/RC default) no
        // exporter is registered, so telemetry is recorded in-process only and there is no export loop or overhead.
        private void AddOpenTelemetryExporters()
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }
        }
    }
}
