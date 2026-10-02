namespace XE_Local_AI_Engine.Client.Hosting;

using Serilog;

/// <summary>
///     Process-wide last-chance records, registered before the host is built: an unhandled exception goes to Fatal and
///     <c>startup-crash.log</c>; an unobserved task exception goes to Error and is marked observed. Never throws.
/// </summary>
internal static class ProcessCrashHooks
{
    private static int _registered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            OnUnhandledException(e.ExceptionObject, Log.Logger, StartupCrashLog.ResolveLogDirectory());
#pragma warning disable MA0045 // Forced sync: the runtime terminates the process when this event handler returns.
            Log.CloseAndFlush();
#pragma warning restore MA0045
        };
        TaskScheduler.UnobservedTaskException += static (_, e) => OnUnobservedTaskException(e, Log.Logger);
    }

    internal static void OnUnhandledException(object exceptionObject, ILogger logger, string? crashLogDirectory)
    {
        try
        {
            var exception = exceptionObject as Exception ?? new InvalidOperationException($"A non-exception object was thrown: {exceptionObject}");
            logger.Fatal(exception, "Unhandled exception; the process is terminating");
            if (crashLogDirectory is not null)
            {
                StartupCrashLog.Record(crashLogDirectory, "Unhandled exception; the process is terminating", exception);
            }
        }
        catch (Exception)
        {
            // swallowed: nothing is left to report to once the logger and the crash log have both failed.
        }
    }

    internal static void OnUnobservedTaskException(UnobservedTaskExceptionEventArgs e, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.SetObserved();
        try
        {
            logger.Error(e.Exception, "Unobserved task exception");
        }
        catch (Exception)
        {
            // swallowed: the task exception is already observed; only its log line is lost.
        }
    }
}
