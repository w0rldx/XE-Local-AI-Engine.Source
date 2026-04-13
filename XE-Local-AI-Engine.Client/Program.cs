using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Events;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Components;

try
{
    var builder = WebApplication.CreateBuilder(args);

    Log.Logger = CreateStartupLogger(builder.Environment);

    // Services
    builder.AddServices(builder.Configuration);

    var app = builder.Build();

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

    app.MapRazorComponents<App>()
       .AddInteractiveServerRenderMode();

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

static Serilog.ILogger CreateStartupLogger(IHostEnvironment environment)
{
    ArgumentNullException.ThrowIfNull(environment);

    var loggerConfiguration = new LoggerConfiguration()
                              .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                              .Enrich.FromLogContext()
                              .WriteTo.Console(outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");

#pragma warning disable CA2000 // Ownership is transferred to Log.Logger and released via Log.CloseAndFlushAsync in finally.
    return environment.IsEnvironment("Testing")
        ? loggerConfiguration.CreateLogger()
        : loggerConfiguration.CreateBootstrapLogger();
#pragma warning restore CA2000
}

public partial class Program
{
    protected Program()
    {
    }
}
