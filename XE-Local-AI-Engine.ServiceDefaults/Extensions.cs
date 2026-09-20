namespace Microsoft.Extensions.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

public static class Extensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        public TBuilder AddServiceDefaults()
        {
            // Registered unconditionally so telemetry works in every hosting mode, not only under Aspire: an operator who sets the
            // standard OTEL_EXPORTER_OTLP_ENDPOINT gets exports, and with none configured the SDK records in-process only.
            builder.ConfigureOpenTelemetry();

            // Service discovery and the global HTTP resilience/discovery defaults resolve the "scheme://service-name" addresses only an
            // Aspire run injects, so they stay gated on ASPIRE_ENABLED — read from configuration, which includes env vars, so it is unit-testable.
            var aspireEnabled = string.Equals(builder.Configuration["ASPIRE_ENABLED"], "true", StringComparison.OrdinalIgnoreCase);

            if (aspireEnabled)
            {
                builder.Services.AddServiceDiscovery();

                builder.Services.ConfigureHttpClientDefaults(http =>
                {
                    http.AddStandardResilienceHandler();

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
                              .AddMeter(TelemetrySourceNames.Node)
                              // Mirrors the identically named tracing AddSource below so the agent's provider-round and
                              // budget counters are exported, not just recorded in-process.
                              .AddMeter(TelemetrySourceNames.Agent)
                              // Flows the OpenTelemetryChatClient's gen_ai token/duration metrics (MEAI meter
                              // "Microsoft.Extensions.AI"); wildcard mirrors the tracing AddSource below.
                              .AddMeter("Microsoft.Extensions.AI*");
                   })
                   .WithTracing(tracing =>
                   {
                       tracing.AddSource(builder.Environment.ApplicationName)
                              // Exports the coarse pre-spawn turn/readiness spans, mirroring the identically
                              // named meters above.
                              .AddSource(TelemetrySourceNames.Node)
                              .AddSource(TelemetrySourceNames.Agent)
                              .AddSource("Microsoft.Agents.AI*")
                              .AddSource("Microsoft.Extensions.AI*")
                              .AddAspNetCoreInstrumentation(tracing =>
                                  tracing.Filter = context =>
                                      !context.Request.Path.StartsWithSegments("/health/live", StringComparison.CurrentCulture))
                              .AddHttpClientInstrumentation()
                              // Downgrade a gen_ai span that failed only because a user pressed Stop (Error→Unset) so a
                              // cancelled turn doesn't read as a service fault on dashboards/alerts.
                              .AddProcessor(new GenAiCancellationStatusProcessor())
                              // Strictly after the cancellation processor, which downgrades a cancelled span to Unset so
                              // a cancellation never reaches this one. Redaction rationale: the processor's remarks.
                              .AddProcessor(new GenAiErrorDescriptionRedactionProcessor());
                   });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        // Added whenever the standard OTEL_EXPORTER_OTLP_ENDPOINT is configured, in any hosting mode: Aspire auto-injects it, so the meters and sources wired above
        // reach the dashboard unconfigured, and a desktop operator pointing it at a collector gets the same path. Unset, no exporter is registered, so nothing exports.
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
