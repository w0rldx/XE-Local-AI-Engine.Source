namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Production <see cref="ILlamaPerplexityRunner" />: one child process, output tail-bounded, tree-killed on cancel.</summary>
internal sealed class LlamaPerplexityRunner : ILlamaPerplexityRunner
{
    /// <summary>
    ///     How much of the child's output is retained. Enough to hold the whole summary block that follows the final
    ///     estimate, and small enough that an operator-visible error message can quote its tail.
    /// </summary>
    private const int MaximumOutputCharacters = 64 * 1024;

    public async Task<LlamaPerplexityProcessResult> RunAsync(string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        var output = new StringBuilder();
        var sink = new object();
        process.OutputDataReceived += (_, args) => Append(output, sink, args.Data);
        process.ErrorDataReceived += (_, args) => Append(output, sink, args.Data);

        if (!process.Start())
        {
            throw new LlamaRuntimeException("The perplexity tool could not be started.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killing the tree matters here beyond tidiness: a base-logit phase that keeps running would go on writing
            // to the temp file this invocation owns, and the caller is about to delete it.
            TryKill(process);
            throw;
        }

        // Drains the async readers, so the tail below is the whole tail rather than whatever had been flushed.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        lock (sink)
        {
            return new LlamaPerplexityProcessResult
            {
                ExitCode = process.ExitCode,
                Output = output.ToString()
            };
        }
    }

    private static void Append(StringBuilder output, object sink, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            _ = output.AppendLine(line);
            if (output.Length > MaximumOutputCharacters)
            {
                _ = output.Remove(0, output.Length - MaximumOutputCharacters);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill. Nothing to clean up.
        }
        catch (SystemException)
        {
            // The OS refused the kill (permissions, a race with reaping). The invocation is failing either way.
        }
    }
}
