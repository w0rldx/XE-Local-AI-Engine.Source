namespace XE_Local_AI_Engine.Client.Common.Extensions;

using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using XE_Local_AI_Engine.Client.Hosting;
using ILogger = Serilog.ILogger;

/// <summary>
///     Represents logger extensions.
/// </summary>
public static class LoggerExtensions
{
    private const string OutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Rolled-file cap (50 MB) — a wedged component cannot fill the disk before the size roll + retention kick in.</summary>
    private const long LogFileSizeLimitBytes = 50L * 1024 * 1024;

    /// <summary>Retain roughly a week of rolled log files so a tester bug report has recent history without unbounded growth.</summary>
    private const int RetainedLogFileCount = 7;

    public static ILogger CreateStartupLogger(this IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        var loggerConfiguration = new LoggerConfiguration()
                                  .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.Http.Connections", LogEventLevel.Warning)
                                  .MinimumLevel.Override("Microsoft.AspNetCore.SignalR", LogEventLevel.Warning)
                                  .Enrich.FromLogContext()
                                  .WriteTo.Console(theme: ConsoleTheme.None, outputTemplate: OutputTemplate);

        // Persist startup output to the rolling file too, so a crash BEFORE the host logger is built (bad config, key/DB
        // failure) is still captured on disk rather than lost with the console window.
        var logDirectory = ResolveLogFileDirectory(environment, configuration);
        if (logDirectory is not null)
        {
            loggerConfiguration = loggerConfiguration.WriteToRollingFile(logDirectory);
        }

#pragma warning disable CA2000 // Ownership is transferred to Log.Logger and released via Log.CloseAndFlushAsync in finally.
        return environment.IsEnvironment("Testing")
            ? loggerConfiguration.CreateLogger()
            : loggerConfiguration.CreateBootstrapLogger();
#pragma warning restore CA2000
    }

    /// <summary>
    ///     Resolves the directory the rolling log file is written to, or <see langword="null" /> when the file sink
    ///     should be disabled. Reuses the SAME per-user data-dir resolution the Data Protection key-ring uses (the
    ///     <see cref="DesktopBootstrap.NodeDataDirectoryKey" /> desktop mode layers in; the content root otherwise), so
    ///     logs land beside <c>node.sqlite</c>/<c>node.key</c> and survive a Velopack update. The <c>Testing</c>
    ///     environment is excluded: the integration/E2E suite spins up many parallel web hosts that would contend for the
    ///     same exclusive log file.
    /// </summary>
    internal static string? ResolveLogFileDirectory(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (environment.IsEnvironment("Testing"))
        {
            return null;
        }

        var root = configuration[DesktopBootstrap.NodeDataDirectoryKey];
        if (string.IsNullOrWhiteSpace(root))
        {
            // Desktop layers NodeData:Directory in before this runs; headless/Aspire/CI leave it unset, so fall back to
            // the content root (mirroring how INodeDataDirectory itself falls back) instead of a volatile temp dir.
            root = environment.ContentRootPath;
        }

        return Path.Combine(root, "logs");
    }

    /// <summary>
    ///     Adds the shared date-rolled file sink under <paramref name="logDirectory" /> (<c>xe-node-.log</c> → the date is
    ///     inserted at the trailing dash). Also rolls on the 50 MB size cap and retains ~7 files. The Serilog file sink
    ///     creates the directory on demand and degrades gracefully (via <c>SelfLog</c>) if it cannot open the file, so a
    ///     bad path never crashes startup.
    /// </summary>
    internal static LoggerConfiguration WriteToRollingFile(this LoggerConfiguration loggerConfiguration, string logDirectory)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        return loggerConfiguration.WriteTo.File(Path.Combine(logDirectory, "xe-node-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: RetainedLogFileCount,
            fileSizeLimitBytes: LogFileSizeLimitBytes,
            rollOnFileSizeLimit: true,
            shared: false,
            outputTemplate: OutputTemplate);
    }
}
