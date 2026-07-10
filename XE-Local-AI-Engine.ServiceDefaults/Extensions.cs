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
            var aspireEnvironment = Environment.GetEnvironmentVariable("ASPIRE_ENABLED");

            if (string.Equals(aspireEnvironment, "true", StringComparison.OrdinalIgnoreCase))
            {
                // Aspire specific services
                builder.ConfigureOpenTelemetry();
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
                              // Flows the OpenTelemetryChatClient's gen_ai token/duration metrics (MEAI meter
                              // "Microsoft.Extensions.AI"); wildcard mirrors the tracing AddSource below.
                              .AddMeter("Microsoft.Extensions.AI*");
                   })
                   .WithTracing(tracing =>
                   {
                       tracing.AddSource(builder.Environment.ApplicationName)
                              .AddSource("XE.LocalAiEngine.AI.Agent")
                              .AddSource("Microsoft.Agents.AI*")
                              .AddSource("Microsoft.Extensions.AI*")
                              .AddAspNetCoreInstrumentation(tracing =>
                                  tracing.Filter = context =>
                                      !context.Request.Path.StartsWithSegments("/health/live", StringComparison.CurrentCulture))
                              .AddHttpClientInstrumentation();
                   });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        // In Aspire-orchestrated dev runs, the AppHost auto-injects OTEL_EXPORTER_OTLP_ENDPOINT into this project's
        // environment (standard AddProject behavior), so the meters/sources wired above (XE.Node, Microsoft.Agents.AI*,
        // Microsoft.Extensions.AI*) already flow to the Aspire dashboard with no extra config here. In desktop mode
        // (XE_LAUNCH_MODE=desktop, no Aspire orchestration) that env var is unset, so this branch is skipped: telemetry
        // is still recorded in-process but never exported. That is intentional until an operator configures an OTLP
        // endpoint for desktop builds.
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
