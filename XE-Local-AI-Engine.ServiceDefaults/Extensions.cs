namespace Microsoft.Extensions.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
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
                              .AddRuntimeInstrumentation();
                   })
                   .WithTracing(tracing =>
                   {
                       tracing.AddSource(builder.Environment.ApplicationName)
                              .AddAspNetCoreInstrumentation(tracing =>
                                  tracing.Filter = context =>
                                      !context.Request.Path.StartsWithSegments("/health/live", StringComparison.CurrentCulture)
                              )
                              .AddHttpClientInstrumentation();
                   });

            builder.AddOpenTelemetryExporters();

            return builder;
        }

        private TBuilder AddOpenTelemetryExporters()
        {
            var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

            if (useOtlpExporter)
            {
                builder.Services.AddOpenTelemetry().UseOtlpExporter();
            }

            return builder;
        }
    }
}
