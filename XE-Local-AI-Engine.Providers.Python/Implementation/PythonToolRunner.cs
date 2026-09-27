namespace XE_Local_AI_Engine.Providers.Python.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Python.Contracts;

/// <summary>Picks the production <see cref="IPythonToolRunner" /> for this OS, and holds the start-and-stream step both share.</summary>
public static class PythonToolRunner
{
    /// <summary>The Windows runner on Windows, the Linux runner everywhere else (which refuses to run off Linux).</summary>
    public static IPythonToolRunner ForCurrentPlatform()
    {
        return OperatingSystem.IsWindows() ? new WindowsPythonToolRunner() : new LinuxPythonToolRunner();
    }

    internal static Process StartStreaming(ProcessStartInfo startInfo, Action<string> logSink)
    {
        var process = new Process
        {
            StartInfo = startInfo
        };
        process.OutputDataReceived += (_, e) => Forward(e.Data, logSink);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, logSink);

        try
        {
            if (!process.Start())
            {
                throw new ManagedPythonException("A managed Python process did not start.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch (ManagedPythonException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new ManagedPythonException("A managed Python process could not be started.", ex);
        }
    }

    private static void Forward(string? line, Action<string> logSink)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        logSink(line);
    }
}
