namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes for and runs the sibling <c>llama-fit-params</c> utility, returning stdout only. llama.cpp deliberately
///     writes diagnostic noise to stderr and the stable replay argument grammar to stdout.
/// </summary>
internal sealed class LlamaFitParamsProcessRunner : ILlamaFitParamsRunner
{
    private static readonly TimeSpan FitTimeout = TimeSpan.FromMinutes(2);

    public async Task<LlamaFitParamsRunResult> RunAsync(LlamaServerLaunchSpec serverSpec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serverSpec);

        var executableName = OperatingSystem.IsWindows() ? "llama-fit-params.exe" : "llama-fit-params";
        var executablePath = Path.Combine(serverSpec.WorkingDirectory, executableName);
        if (!File.Exists(executablePath))
        {
            return LlamaFitParamsRunResult.Missing();
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = serverSpec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in BuildArguments(serverSpec.Arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return LlamaFitParamsRunResult.Failure("The helper process did not start.");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return LlamaFitParamsRunResult.Failure("The helper process could not be started.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(FitTimeout);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return LlamaFitParamsRunResult.Failure($"The helper exited with code {process.ExitCode}.");
            }

            var output = stdout.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return output.Length == 0
                ? LlamaFitParamsRunResult.Failure("The helper emitted no machine-readable stdout.")
                : LlamaFitParamsRunResult.Success(output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return LlamaFitParamsRunResult.Failure("The helper exceeded its bounded execution time.");
        }
        finally
        {
            TryKill(process);
        }
    }

    /// <summary>
    ///     Projects the server launch vector onto the common llama.cpp options that affect fit estimation. Server-only
    ///     transport, metrics, warm-up, and template flags are deliberately omitted.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> serverArguments)
    {
        ArgumentNullException.ThrowIfNull(serverArguments);

        var result = new List<string>();
        for (var index = 0; index < serverArguments.Count; index++)
        {
            var argument = serverArguments[index];
            if (TakesValue(argument))
            {
                if (index + 1 >= serverArguments.Count)
                {
                    break;
                }

                result.Add(argument);
                result.Add(serverArguments[++index]);
            }
            else if (IsValueLessFitArgument(argument))
            {
                result.Add(argument);
            }
        }

        return result;
    }

    private static bool TakesValue(string argument) =>
        argument is "-m" or "--model"
            or "-c" or "--ctx-size"
            or "-b" or "--batch-size"
            or "-ub" or "--ubatch-size"
            or "-np" or "--parallel"
            or "-fa" or "--flash-attn"
            or "-ctk" or "--cache-type-k"
            or "-ctv" or "--cache-type-v"
            or "-dev" or "--device"
            or "-sm" or "--split-mode"
            or "-mg" or "--main-gpu"
            or "-fit" or "--fit"
            or "-fitt" or "--fit-target"
            or "-fitc" or "--fit-ctx"
            or "--pooling";

    private static bool IsValueLessFitArgument(string argument) =>
        argument is "--mlock" or "--mmap" or "--no-mmap" or "--no-host" or "--no-op-offload";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort: the process may exit between the check and kill, or may already be unavailable.
        }
    }
}
