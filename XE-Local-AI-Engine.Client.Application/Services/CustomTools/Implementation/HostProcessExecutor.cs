namespace XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Executes a <c>Command</c> custom tool on the host, porting the sandbox provider's host-exec posture minus the jail.</summary>
/// <remarks>
///     <see cref="ProcessStartInfo.ArgumentList" /> is used, never a shell string, so a substituted value is always one argv element; shell
///     execution is off; the environment is cleared, repopulated from the system allow-list and overlaid with the tool's own env, so secrets
///     travel by env and never argv; a linked-CTS wall-clock timeout tree-kills; each stream has an output byte cap. The executable is
///     validated at execution time (absolute, non-interpreter, real regular file, no symlink follow), the run is admitted through the global
///     concurrency limiter, and secret env values are scrubbed from stdout and stderr.
/// </remarks>
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
    private const int DefaultTimeoutSeconds = 30;
    private const string UnixShell = "/bin/sh";

    // "$0" is the operator's executable and "$@" its arguments, both positional: the script text never changes.
    private const string UnixNullSinkScript = "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1";

    /// <summary>
    ///     How long a <see cref="CommandConfig.Detach" /> launch, which keeps every guard but neither captures output nor waits, watches for an
    ///     immediate crash before reporting the program as started.
    /// </summary>
    internal static readonly TimeSpan DetachGrace = TimeSpan.FromMilliseconds(500);

    private readonly CustomToolConcurrencyLimiter _concurrencyLimiter;
    private readonly ILogger<HostProcessExecutor> _logger;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly TimeProvider _timeProvider;

    public HostProcessExecutor(CustomToolConcurrencyLimiter concurrencyLimiter,
        INodeRuntimeSettings runtimeSettings,
        TimeProvider timeProvider,
        ILogger<HostProcessExecutor> logger)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _concurrencyLimiter = concurrencyLimiter ?? throw new ArgumentNullException(nameof(concurrencyLimiter));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public CustomToolKind Kind => CustomToolKind.Command;

    public async Task<CustomToolExecution> ExecuteAsync(CustomToolRecord tool, string jsonArguments, CancellationToken cancellationToken)
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
            return CustomToolExecution.Failure($"The custom tool call was blocked: {redactor.Redact(exception.Message)}");
        }
        catch (CustomToolConfigurationException exception)
        {
            _logger.LogWarning("Custom tool {ToolName} has invalid configuration: {Reason}", tool.Name, exception.Message);
            return CustomToolExecution.Failure("The custom tool is misconfigured and could not run.");
        }

        using var slot = await _concurrencyLimiter.AcquireAsync(cancellationToken);
        if (config.Detach)
        {
            return await LaunchDetachedAsync(startInfo, redactor, cancellationToken);
        }

        // The ceiling is the node's CustomToolMaxTimeoutSeconds, the same value CustomToolService validates a saved timeout against.
        var maxTimeoutSeconds = await _runtimeSettings.GetCustomToolMaxTimeoutSecondsAsync(cancellationToken);
        return await RunAsync(tool.Name, startInfo, config, maxTimeoutSeconds, redactor, cancellationToken);
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

        // Start the child from a scrubbed environment: ProcessStartInfo pre-seeds the FULL worker environment, which holds secrets, so clear it and
        // repopulate only allow-listed system and toolchain variables, then overlay the tool's own env — never argv, where /proc/<pid>/cmdline leaks.
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
            // Each template element expands into exactly ONE argv element, with no encoding, and that single-argv guarantee IS the injection control.
            // No synthetic end-of-options marker is injected: it corrupts the common flag-then-value shape and the security property does not need it.
            arguments.Add(CustomToolTemplate.Substitute(template, bound, declaredNames));
        }

        return arguments;
    }

    /// <summary>Starts the program and reports started, or the exit code it died with inside the grace; never waits for it.</summary>
    /// <remarks>
    ///     The program outlives this call and the engine, so its output is not ours to read, yet it must not inherit the
    ///     engine's streams either: a program echoing an injected secret would write it past the redactor into the engine's
    ///     console. On Unix the OS null device takes both streams, so the program survives an engine restart; on Windows the
    ///     streams are pipes drained to nothing for as long as the program runs. A GUI program keeps its window.
    /// </remarks>
    private async Task<CustomToolExecution> LaunchDetachedAsync(ProcessStartInfo startInfo,
        SecretValueRedactor redactor,
        CancellationToken cancellationToken)
    {
        var launch = NullSinkLaunch(startInfo) ?? startInfo;
        var drained = launch.RedirectStandardOutput;

        var process = new Process
        {
            StartInfo = launch
        };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return CustomToolExecution.Failure("The custom tool executable could not be launched.");
        }

#pragma warning disable CA2025 // The release task below owns the Process; nothing disposes it before the drain ends.
        var drain = drained ? DiscardOutputAsync(process) : Task.CompletedTask;
#pragma warning restore CA2025
        try
        {
            var name = Path.GetFileName(startInfo.FileName);
            var processId = process.Id;
            using var graceSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exited = process.WaitForExitAsync(graceSource.Token);
            var first = await Task.WhenAny(exited, Task.Delay(DetachGrace, _timeProvider, graceSource.Token));
            await graceSource.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();

            if (first == exited && process.ExitCode != 0)
            {
                return CustomToolExecution.Failure(redactor.Redact(
                    string.Create(CultureInfo.InvariantCulture, $"{name} (process {processId}) exited at launch.\nexit_code: {process.ExitCode}\n")));
            }

            return CustomToolExecution.Success(redactor.Redact(string.Create(CultureInfo.InvariantCulture, $"started {name} (process {processId})\n")));
        }
        finally
        {
            // The handle is released once the launch check AND the drain are done; the program itself is never killed.
#pragma warning disable CA2025 // The release task owns the Process from here on.
            _ = ReleaseWhenDrainedAsync(process, drain);
#pragma warning restore CA2025
        }
    }

    /// <summary>
    ///     On Unix, the launch that hands the program the OS null device: the system shell replaces itself with the
    ///     program (<c>exec</c>) after pointing all three standard streams at it, so no pipe ties the program to the engine.
    /// </summary>
    /// <remarks>
    ///     The operator's executable and every argument travel as positional parameters, never inside the fixed script, so
    ///     nothing is re-parsed; the cleared environment is copied over unchanged. Null on Windows or without the shell.
    /// </remarks>
    private static ProcessStartInfo? NullSinkLaunch(ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(UnixShell))
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = false;
            return null;
        }

        var launch = new ProcessStartInfo(UnixShell)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            WorkingDirectory = startInfo.WorkingDirectory
        };
        launch.ArgumentList.Add("-c");
        launch.ArgumentList.Add(UnixNullSinkScript);
        launch.ArgumentList.Add(startInfo.FileName);
        foreach (var argument in startInfo.ArgumentList)
        {
            launch.ArgumentList.Add(argument);
        }

        launch.Environment.Clear();
        foreach (var variable in startInfo.Environment)
        {
            launch.Environment[variable.Key] = variable.Value;
        }

        return launch;
    }

    /// <summary>Drains a detached program's pipes to nothing so a full pipe never blocks it (the Windows path).</summary>
    private static async Task DiscardOutputAsync(Process process)
    {
        try
        {
            await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None),
                process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None));
        }
        catch (IOException)
        {
            // The program closed its end; nothing to keep.
        }
    }

    private static async Task ReleaseWhenDrainedAsync(Process process, Task drain)
    {
        try
        {
            await drain;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<CustomToolExecution> RunAsync(string toolName,
        ProcessStartInfo startInfo,
        CommandConfig config,
        int maxTimeoutSeconds,
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
            return CustomToolExecution.Failure("The custom tool executable could not be launched.");
        }

        var timeoutSeconds = Math.Clamp(config.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : config.TimeoutSeconds,
            MinTimeoutSeconds,
            maxTimeoutSeconds);

        using var timeoutSource = new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var exitCode = process.ExitCode;
            var text = FormatResult(toolName, exitCode, standardOutput, standardError, redactor, timedOut: false);
            return exitCode == 0 ? CustomToolExecution.Success(text) : CustomToolExecution.Failure(text);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TreeKill(process);
            return CustomToolExecution.Failure(FormatResult(toolName, exitCode: -1, standardOutput, standardError, redactor, timedOut: true));
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

    private static string FormatResult(string toolName,
        int exitCode,
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

        // Scrub secret environment values from the program output: any program the command runs can read its injected
        // env by design, so echoing a secret back through stdout/stderr is the leak this closes. The output is then fenced.
        var output = string.Concat("stdout:\n", standardOutput.ToStringWithMarker(), "\nstderr:\n", standardError.ToStringWithMarker());
        builder.Append(UntrustedContentFraming.WrapDocument(redactor.Redact(output), [new("tool", toolName), new("kind", "command")]));
        return builder.ToString();
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
    ///     A thread-safe accumulator with a hard UTF-8 byte ceiling, so a runaway command cannot exhaust memory while the event pump keeps
    ///     draining the pipe.
    /// </summary>
    /// <remarks>A compact analogue of the sandbox provider's private capped builder, which is not reachable from here.</remarks>
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
