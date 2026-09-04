namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes for and runs the sibling <c>llama-fit-params</c> utility, returning stdout only. llama.cpp deliberately
///     writes diagnostic noise to stderr and the stable replay argument grammar to stdout.
/// </summary>
internal sealed class LlamaFitParamsProcessRunner : ILlamaFitParamsRunner
{
    private const int MaxStandardErrorExcerptLength = 240;
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
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return FailureWithStandardError($"The helper exited with code {process.ExitCode}.",
                    stderr,
                    serverSpec.WorkingDirectory);
            }

            var output = stdout.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return output.Length == 0
                ? FailureWithStandardError("The helper emitted no machine-readable stdout.",
                    stderr,
                    serverSpec.WorkingDirectory)
                : LlamaFitParamsRunResult.Success(output);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return LlamaFitParamsRunResult.Failure("The helper exceeded its bounded execution time.");
        }
        finally
        {
            ProcessCaptureRunner.TryKill(process);
        }
    }

    /// <summary>
    ///     Projects the server launch vector onto the common llama.cpp options that affect fit estimation. Server-only
    ///     transport, metrics, warm-up, template, and role flags are deliberately omitted.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> serverArguments)
    {
        ArgumentNullException.ThrowIfNull(serverArguments);

        var result = new List<string>();
        var index = 0;
        while (index < serverArguments.Count)
        {
            var argument = serverArguments[index];
            if (TakesValue(argument))
            {
                if (index + 1 >= serverArguments.Count)
                {
                    break;
                }

                result.Add(argument);
                result.Add(serverArguments[index + 1]);
                index += 2;
                continue;
            }

            if (IsValueLessFitArgument(argument))
            {
                result.Add(argument);
            }

            index++;
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
            or "-ncmoe" or "--n-cpu-moe";

    // --cpu-moe/--n-cpu-moe are expert placement, and upstream pushes them into the same tensor_buft_overrides list
    // -ot writes (llama.cpp common/arg.cpp). The helper both fits AGAINST those overrides and echoes them back as
    // -ot "<pattern>=CPU" (tools/fit-params/fit-params.cpp), so passing them through is what makes the fitted -ngl
    // honest and gives the frozen replay a concrete expert placement. Dropping them fits the wrong placement.
    private static bool IsValueLessFitArgument(string argument) =>
        argument is "--mlock" or "--mmap" or "--no-mmap" or "--no-host" or "--no-op-offload"
            or "-cmoe" or "--cpu-moe";

    private static LlamaFitParamsRunResult FailureWithStandardError(string reason,
        string standardError,
        string workingDirectory)
    {
        var excerpt = SanitizeStandardError(standardError, workingDirectory);
        return LlamaFitParamsRunResult.Failure(excerpt.Length == 0 ? reason : $"{reason} {excerpt}");
    }

    private static string SanitizeStandardError(string standardError, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return string.Empty;
        }

        var excerpt = string.Join(' ',
            standardError.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            excerpt = excerpt.Replace(workingDirectory, "<runtime>", StringComparison.Ordinal);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            excerpt = excerpt.Replace(home, "<home>", StringComparison.Ordinal);
        }

        excerpt = Regex.Replace(excerpt,
            @"(?i)\b(?<name>token|api[_-]?key|secret|password)\s*=\s*[^\s]+",
            "${name}=<redacted>",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(100));
        excerpt = Regex.Replace(excerpt,
            @"(?<![\p{L}\p{N}_])(?:[A-Za-z]:[\\/]|/)[^\s""']+",
            "<path>",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromMilliseconds(100));
        excerpt = new string(excerpt.Where(static character => !char.IsControl(character)).ToArray()).Trim();
        return excerpt.Length <= MaxStandardErrorExcerptLength
            ? excerpt
            : string.Concat(excerpt.AsSpan(0, MaxStandardErrorExcerptLength - 1), "…");
    }
}
