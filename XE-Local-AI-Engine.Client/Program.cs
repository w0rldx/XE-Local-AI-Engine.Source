using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using FastEndpoints;
using FastEndpoints.Swagger;
using Scalar.AspNetCore;
using Serilog;
using Microsoft.Agents.AI.DevUI;
using XE_Local_AI_Engine.AI.Agent.DependencyInjection;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Common.Extensions;
using XE_Local_AI_Engine.Client.Components;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Shutdown;

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();

    builder.Host.UseDefaultServiceProvider((context, options) =>
    {
        var isDevelopment = context.HostingEnvironment.IsDevelopment();
        options.ValidateScopes = isDevelopment;
        options.ValidateOnBuild = isDevelopment;
    });

    Log.Logger = builder.Environment.CreateStartupLogger();

    // Aspire services
    builder.AddServiceDefaults();

    // Add services to the container.
    builder.AddServices(builder.Configuration);

    // Agent Framework DevUI (development only): a representative named agent plus the
    // OpenAI-compatible Responses/Conversations services the DevUI dashboard requires.
    if (builder.Environment.IsDevelopment())
    {
        builder.AddLocalAiAgentDevUi();
        builder.AddOpenAIResponses();
        builder.AddOpenAIConversations();
        builder.AddDevUI();
    }

    var app = builder.Build();

    await ApplyNodeChatMigrationsAsync(app.Services).ConfigureAwait(false);
    RegisterWorkerShutdownDrain(app);

    app.UseSerilogRequestLogging();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", true);
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseAntiforgery();

    app.UseStaticFiles();
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false // don't run any checks; just return 200 if the app can serve requests
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = r => r.Tags.Contains("ready"),
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var payload = new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    duration = e.Value.Duration.TotalMilliseconds
                })
            };
            await context.Response.WriteAsJsonAsync(payload);
        }
    });

    app.UseMiddleware<LocalApiSecurityMiddleware>();
    app.UseAuthentication();
    app.UseAuthorization();

    app.UseFastEndpoints(static config =>
    {
        config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;
        config.Errors.UseProblemDetails();
    });

    if (!app.Environment.IsProduction())
    {
        app.UseSwaggerGen(static options =>
        {
            options.Path = "/openapi/local/v1/{documentName}.json";
        });

        app.MapScalarApiReference("/scalar", static settings =>
        {
            settings.OpenApiRoutePattern = "/openapi/local/{documentName}/{documentName}.json";

            settings.AddDocument("v1");

            settings.AddPreferredSecuritySchemes("LocalOperator");
        }).AllowAnonymous();
    }

    // Agent Framework DevUI dashboard (development only) at /devui. The OpenAI-compatible
    // Responses + Conversations endpoints must be mapped before MapDevUI.
    if (app.Environment.IsDevelopment())
    {
        app.MapOpenAIResponses();
        app.MapOpenAIConversations();
        app.MapDevUI();
    }

    app.MapRazorComponents<App>()
       .AddInteractiveServerRenderMode();

    app.MapFallbackToFile("/app/{*path:nonfile}", "app/index.html");

    await app.RunAsync();
}
catch (HostAbortedException)
{
    Log.Information("The Application was aborted");
}
catch (Exception ex)
{
    Log.Fatal(ex, "The Application failed to start");
    throw;
}
finally
{
    Log.Information("Application Stopping");
    await Log.CloseAndFlushAsync();
}

static async Task ApplyNodeChatMigrationsAsync(IServiceProvider services)
{
    ArgumentNullException.ThrowIfNull(services);

    await using var scope = services.CreateAsyncScope();
    var migrationService = scope.ServiceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

    await migrationService.MigrateAsync().ConfigureAwait(false);
}

static void RegisterWorkerShutdownDrain(WebApplication app)
{
    ArgumentNullException.ThrowIfNull(app);

    app.Lifetime.ApplicationStopping.Register(static state =>
    {
        var services = (IServiceProvider)state!;

        try
        {
            var drainService = services.GetRequiredService<IWorkerShutdownDrainService>();
            var result = drainService.DrainAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (!result.Succeeded)
            {
                Log.Warning("Worker shutdown drain completed with incomplete steps. Diagnostics: {Diagnostics}.", result.Diagnostics);
            }
        }
        catch (Exception exception)
        {
            Log.Error("Worker shutdown drain failed before completion. Exception type: {ExceptionType}.",
                exception.GetType().Name);
        }
    }, app.Services);
}

namespace XE_Local_AI_Engine.Client
{
    public class Program
    {
        protected Program()
        {
        }
    }
}
