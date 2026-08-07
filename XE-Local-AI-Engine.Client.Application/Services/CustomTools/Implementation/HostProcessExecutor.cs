namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Executes a <c>Command</c> custom tool on the host. Ports the sandbox provider's proven host-exec posture minus
///     the jail/launcher: <see cref="ProcessStartInfo.ArgumentList" /> (never a shell string, so a substituted value is
///     always one argv element), <c>UseShellExecute=false</c>, a scrubbed environment (cleared, repopulated from the
///     system allow-list, then the tool's own env overlaid — secrets via env, never argv), a linked-CTS wall-clock
///     timeout with tree-kill, and a per-stream output byte cap. The executable is validated at execution time
///     (absolute, non-interpreter, real regular file via <c>O_NOFOLLOW</c>), and the whole run is admitted through the
///     global concurrency limiter. Secret env values are scrubbed from stdout/stderr before the model sees them.
/// </summary>
internal sealed class HostProcessExecutor : ICustomToolExecutor
{
    // The system and toolchain variables a host command may inherit, mirroring the sandbox provider's allow-list. No
    // secret-bearing worker variable appears in this set, because the child starts from an empty environment.
    private static readonly string[] InheritableEnvironmentAllowlist =
    [
        "PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL",
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO",
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA"
    ];

    private const int MaxCapturedOutputBytes = 64 * 1024;
    private const int MinTimeoutSeconds = 1;
    private const int MaxTimeoutSeconds = 300;
    private const int DefaultTimeoutSeconds = 30;

    private readonly CustomToolConcurrencyLimiter _concurrencyLimiter;
    private readonly ILogger<HostProcessExecutor> _logger;

    public HostProcessExecutor(CustomToolConcurrencyLimiter concurrencyLimiter, ILogger<HostProcessExecutor> logger)
    {
        _concurrencyLimiter = concurrencyLimiter ?? throw new ArgumentNullException(nameof(concurrencyLimiter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CustomToolKind Kind => CustomToolKind.Command;

    public async Task<string> ExecuteAsync(CustomToolRecord tool, string jsonArguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);

        CommandConfig config;
        // Definite-assigned to the empty-set redactor so a guard exception thrown before config parses is still
        // scrubbed (userinfo-only) rather than reusing a bare inline redactor per catch.
        var redactor = new SecretValueRedactor([]);
        ProcessStartInfo startInfo;
        try
        {
            config = CustomToolConfigParser.ParseCommand(tool.ConfigJson);
            var parameters = CustomToolConfigParser.ParseParameters(tool.ParametersJson);
            redactor = new SecretValueRedactor(config.Env.Where(static variable => variable.IsSecret).Select(static variable => variable.Value));

            HostExecutableGuard.Validate(config.Executable);
            startInfo = BuildStartInfo(config, parameters, jsonArguments);
        }
        catch (CustomToolExecutionException exception)
        {
            return $"The custom tool call was blocked: {redactor.Redact(exception.Message)}";
        }
        catch (CustomToolConfigurationException exception)
        {
            _logger.LogWarning("Custom tool {ToolName} has invalid configuration: {Reason}", tool.Name, exception.Message);
            return "The custom tool is misconfigured and could not run.";
        }

        using var slot = await _concurrencyLimiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(startInfo, config, redactor, cancellationToken).ConfigureAwait(false);
    }

    private static ProcessStartInfo BuildStartInfo(CommandConfig config,
        IReadOnlyList<CustomToolParameter> parameters,
        string jsonArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = config.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            if (!CustomToolValidation.IsAbsolutePath(config.WorkingDirectory) || !Directory.Exists(config.WorkingDirectory))
            {
                throw new CustomToolExecutionException("The command tool's working directory must be an existing absolute path.");
            }

            startInfo.WorkingDirectory = config.WorkingDirectory;
        }

        foreach (var argument in BuildArguments(config.ArgsTemplate, parameters, jsonArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Start the child from a scrubbed environment: ProcessStartInfo pre-seeds the FULL worker environment (which
        // holds secrets), so clear it and repopulate only the allow-listed system/toolchain variables, then overlay the
        // tool's own env (secrets travel here, in the environment, never on argv where /proc/<pid>/cmdline would leak
        // them).
        startInfo.Environment.Clear();
        foreach (var name in InheritableEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var variable in config.Env.Where(static variable => !string.IsNullOrEmpty(variable.Name)))
        {
            startInfo.Environment[variable.Name] = variable.Value;
        }

        return startInfo;
    }

    private static IReadOnlyList<string> BuildArguments(IReadOnlyList<string> argsTemplate,
        IReadOnlyList<CustomToolParameter> parameters,
        string jsonArguments)
    {
        var bound = CustomToolTemplate.BindAndEnforce(jsonArguments, parameters);
        var declaredNames = parameters.Select(static parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);

        var arguments = new List<string>(argsTemplate.Count);
        foreach (var template in argsTemplate)
        {
            // Each template element expands into exactly ONE argv element (no encoding — argv is not a URL, and
            // BindAndEnforce + Substitute never split a value across argv). That single-argv guarantee is the injection
            // control. We deliberately do NOT inject a synthetic "--" end-of-options marker: it corrupts the common
            // ["--flag", "{value}"] shape (the value is not a positional, so "--flag -- value" makes the parser read
            // "--" as the flag's value), and it is not needed for the security property. An operator who wants
            // end-of-options handling authors it in the template itself.
            arguments.Add(CustomToolTemplate.Substitute(template, bound, declaredNames));
        }

        return arguments;
    }

    private static async Task<string> RunAsync(ProcessStartInfo startInfo,
        CommandConfig config,
        SecretValueRedactor redactor,
        CancellationToken cancellationToken)
    {
        var standardOutput = new CappedOutput(MaxCapturedOutputBytes);
        var standardError = new CappedOutput(MaxCapturedOutputBytes);

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, args) => standardOutput.AppendLine(args.Data);
        process.ErrorDataReceived += (_, args) => standardError.AppendLine(args.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return "The custom tool executable could not be launched.";
        }

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : config.TimeoutSeconds,
            MinTimeoutSeconds,
            MaxTimeoutSeconds);

        using var timeoutSource = new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            return FormatResult(process.ExitCode, standardOutput, standardError, redactor, timedOut: false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TreeKill(process);
            return FormatResult(exitCode: -1, standardOutput, standardError, redactor, timedOut: true);
        }
        catch (OperationCanceledException)
        {
            TreeKill(process);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string FormatResult(int exitCode,
        CappedOutput standardOutput,
        CappedOutput standardError,
        SecretValueRedactor redactor,
        bool timedOut)
    {
        var builder = new StringBuilder();
        if (timedOut)
        {
            builder.Append(CultureInfo.InvariantCulture, $"The custom tool timed out and was terminated.\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"exit_code: {exitCode}\n");
        builder.Append("stdout:\n");
        builder.Append(standardOutput.ToStringWithMarker());
        builder.Append("\nstderr:\n");
        builder.Append(standardError.ToStringWithMarker());

        // Scrub secret env values from the whole model-facing string (H4): any program the command runs can read its
        // injected env by design, so echoing a secret back through stdout/stderr is the leak this closes.
        return redactor.Redact(builder.ToString());
    }

    private static void TreeKill(Process process)
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
            // Already exited between the check and the kill.
        }
        catch (NotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }
    }

    /// <summary>
    ///     A thread-safe accumulator with a hard UTF-8 byte ceiling, so a runaway command cannot exhaust memory while the
    ///     event pump keeps draining the pipe. A compact analogue of the sandbox provider's private capped builder
    ///     (which is not reachable from here).
    /// </summary>
    private sealed class CappedOutput
    {
        private readonly StringBuilder _builder = new();
        private readonly int _capBytes;
        private readonly Lock _sync = new();
        private int _byteLength;
        private bool _capped;

        public CappedOutput(int capBytes)
        {
            _capBytes = capBytes;
        }

        public void AppendLine(string? data)
        {
            if (data is null)
            {
                return;
            }

            lock (_sync)
            {
                if (_capped)
                {
                    return;
                }

                const int newlineBytes = 1;
                var remaining = _capBytes - _byteLength - newlineBytes;
                if (remaining < 0)
                {
                    _capped = true;
                    return;
                }

                var lineBytes = Encoding.UTF8.GetByteCount(data);
                var toAppend = data;
                if (lineBytes > remaining)
                {
                    toAppend = TruncateToUtf8ByteBudget(data, remaining);
                    _capped = true;
                }

                _builder.Append(toAppend).Append('\n');
                _byteLength += Encoding.UTF8.GetByteCount(toAppend) + newlineBytes;
            }
        }

        public string ToStringWithMarker()
        {
            lock (_sync)
            {
                return _capped ? _builder + "…[output truncated]" : _builder.ToString();
            }
        }

        private static string TruncateToUtf8ByteBudget(string value, int budget)
        {
            if (budget <= 0)
            {
                return string.Empty;
            }

            var used = 0;
            var lastCharIndex = 0;
            var charIndex = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (used + rune.Utf8SequenceLength > budget)
                {
                    break;
                }

                used += rune.Utf8SequenceLength;
                charIndex += rune.Utf16SequenceLength;
                lastCharIndex = charIndex;
            }

            return value[..lastCharIndex];
        }
    }
}
