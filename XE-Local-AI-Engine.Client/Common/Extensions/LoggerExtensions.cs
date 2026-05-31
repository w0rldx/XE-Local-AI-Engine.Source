namespace XE_Local_AI_Engine.Client.Common.Extensions;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using ILogger = Serilog.ILogger;

/// <summary>
///     Represents logger extensions.
/// </summary>
public static class LoggerExtensions
{
    public static ILogger CreateStartupLogger(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        const string ConsoleOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

        var loggerConfiguration = new LoggerConfiguration()
                                  .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.Http.Connections", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", LogEventLevel.Warning)
                                  .Enrich.FromLogContext()
                                  .WriteTo.Console(theme: ConsoleTheme.None, outputTemplate: ConsoleOutputTemplate);

#pragma warning disable CA2000 // Ownership is transferred to Log.Logger and released via Log.CloseAndFlushAsync in finally.
        return environment.IsEnvironment("Testing")
            ? loggerConfiguration.CreateLogger()
            : loggerConfiguration.CreateBootstrapLogger();
#pragma warning restore CA2000
    }
}
