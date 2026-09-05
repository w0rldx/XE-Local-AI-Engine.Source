namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
public sealed class EngineCliProcessTests : IDisposable
{
    /// <summary>The engine's deterministic "--port is taken" exit code; it never falls back to another port.</summary>
    private const int PortInUseExitCode = 6;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-engine-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task SetupAndMcpKey_EnforcePrerequisiteIdempotencyOutputAndEnvOnlyExitRule()
    {
        Directory.CreateDirectory(_root);

        var beforeSetup = await RunAsync(["--mcp-key", "delegate"], launchMode: null).ConfigureAwait(false);
        AssertEx.Equal(expected: 5, beforeSetup.ExitCode, beforeSetup.CombinedOutput);
        AssertEx.False(beforeSetup.StandardOutput.Contains("XE_MCP_KEY=", StringComparison.Ordinal));

        var firstSetup = await RunAsync(["--setup"], DesktopLaunch.McpOnlyModeValue).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, firstSetup.ExitCode, firstSetup.CombinedOutput);
        AssertEx.Contains(firstSetup.StandardOutput, "XE_SETUP=created");
        AssertEx.Contains(firstSetup.StandardOutput, "XE_ADMIN_EMAIL=agent@example.test");
        AssertEx.False(File.Exists(Path.Combine(_root, DesktopPortStore.ReadyFileName)),
            "An environment-only launch mode must not turn setup into a serving process.");

        var secondSetup = await RunAsync(["--setup"], launchMode: null).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, secondSetup.ExitCode, secondSetup.CombinedOutput);
        AssertEx.Contains(secondSetup.StandardOutput, "XE_SETUP=already-configured");
        AssertEx.False(secondSetup.StandardOutput.Contains("XE_ADMIN_EMAIL=", StringComparison.Ordinal));

        var agentic = await RunAsync(["--mcp-key=agentic"], launchMode: null).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, agentic.ExitCode, agentic.CombinedOutput);
        AssertEx.Equal(expected: 1,
            agentic.StandardOutput.Split(Environment.NewLine)
                   .Count(static line => line.StartsWith("XE_MCP_KEY=xemcp_", StringComparison.Ordinal)));
        AssertEx.False(agentic.StandardError.Contains("agentic scope is not yet enforced", StringComparison.Ordinal),
            "The CLI must stop claiming agentic scope is unenforced once scope persistence and policy enforcement ship.");
    }

    [Test]
    public async Task McpOnlyPrimaryServe_EmitsCanonicalReadinessSupportsStatusAndEnforcesPortAndLeaseExits()
    {
        Directory.CreateDirectory(_root);

        // A port number obtained by binding :0 and releasing is only a candidate — another process on
        // the box can claim it before this child binds it, and the engine then exits with
        // PortInUseExitCode. Retry on that signal with a fresh candidate rather than trusting the
        // released port. The port-is-honoured property below still asserts the exact winning number.
        var serving = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var started = StartServing(["--setup", "--mcp-only", "--port", candidate.ToString(CultureInfo.InvariantCulture)]);
            var line = await started.ReadReadyLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                await started.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            return new ServingEngine(started, candidate, line);
        }).ConfigureAwait(false);

        await using var engine = serving.Engine;
        var port = serving.Port;
        var readyLine = serving.ReadyLine;
        var ready = AssertEx.NotNull(DesktopPortStore.ReadReady(_root));

        AssertEx.Equal($"XE_READY=1 XE_VERSION={ready.Version} XE_URL={ready.Url} XE_MCP_URL={ready.McpUrl} XE_DATA_DIR={ready.DataDir}", readyLine);
        AssertEx.Equal($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}", ready.Url);
        AssertEx.Equal($"{ready.Url}/api/local/v1/mcp/server", ready.McpUrl);
        AssertEx.Equal(_root, ready.DataDir);
        AssertEx.True(engine.StandardOutput.Contains("XE_SETUP=created", StringComparison.Ordinal));
        var status = await RunAsync(["--status", "--json"], launchMode: null).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, status.ExitCode, status.CombinedOutput);
        using (var document = JsonDocument.Parse(status.StandardOutput))
        {
            AssertEx.True(document.RootElement.GetProperty("running").GetBoolean());
            AssertEx.False(document.RootElement.GetProperty("setupRequired").GetBoolean());
            AssertEx.Equal(ready.Url, document.RootElement.GetProperty("url").GetString());
        }

        var leaseConflict = await RunAsync(["--mcp-key", "delegate"], launchMode: null).ConfigureAwait(false);
        AssertEx.Equal(expected: 4, leaseConflict.ExitCode, leaseConflict.CombinedOutput);

        var occupiedRoot = Path.Combine(Path.GetTempPath(), "xe-engine-cli-occupied-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(occupiedRoot);
        // The port is the subject here, so hold the listener rather than releasing a candidate: the
        // engine must see it occupied for the whole child run.
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var occupied = await RunAsync(["--mcp-only", "--port", occupiedPort.ToString(CultureInfo.InvariantCulture)],
            launchMode: null,
            occupiedRoot).ConfigureAwait(false);
        AssertEx.Equal(PortInUseExitCode, occupied.ExitCode, occupied.CombinedOutput);
        Directory.Delete(occupiedRoot, recursive: true);
    }

    [Test]
    public async Task MalformedExplicitAdminEmail_IsProcessLevelUsageFailure()
    {
        var result = await RunAsync(["--setup", "--admin-email="], launchMode: null).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, result.ExitCode, result.CombinedOutput);
        AssertEx.Contains(result.StandardError, "--admin-email");
    }

    [Test]
    public async Task Setup_WhenFilesystemPreparationFails_ReturnsRedactedExitOneInsteadOfRuntimeAbort()
    {
        var blockedDataPath = Path.Combine(Path.GetTempPath(), "xe-engine-cli-blocked-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blockedDataPath, "not-a-directory").ConfigureAwait(false);
        try
        {
            var result = await RunAsync(["--setup"], launchMode: null, blockedDataPath).ConfigureAwait(false);

            AssertEx.Equal(expected: 1, result.ExitCode, result.CombinedOutput);
            AssertEx.Contains(result.StandardError,
                "The engine command failed unexpectedly (stage=host-initialization, type=DesktopDataDirectoryException).");
            AssertEx.Contains(result.StandardError,
                $"The data directory could not be created. Verify {DesktopBootstrap.DataDirectoryEnvironmentVariable} and filesystem permissions.");
            AssertEx.False(result.CombinedOutput.Contains("!Demo1234567", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(blockedDataPath);
        }
    }

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments, string? launchMode, string? dataDirectory = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments, launchMode, dataDirectory ?? _root)
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The engine CLI process could not be started.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CommandResult(process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private RunningEngine StartServing(IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(arguments, launchMode: null, _root)
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The engine serving process could not be started.");
        }

        return new RunningEngine(process);
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments, string? launchMode, string dataDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        startInfo.Environment["WorkerNode__NodeName"] = "engine-cli-test";
        startInfo.Environment[DesktopBootstrap.DataDirectoryEnvironmentVariable] = dataDirectory;
        startInfo.Environment[DesktopLaunch.AdminEmailEnvironmentVariable] = "agent@example.test";
        startInfo.Environment[DesktopLaunch.AdminPasswordEnvironmentVariable] = "!Demo1234567";
        if (launchMode is not null)
        {
            startInfo.Environment[DesktopLaunch.LaunchModeEnvironmentVariable] = launchMode;
        }

        return startInfo;
    }

    private sealed record ServingEngine(RunningEngine Engine, int Port, string ReadyLine);

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => string.Concat(StandardOutput, Environment.NewLine, StandardError);
    }

    private sealed class RunningEngine : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private readonly List<string> _standardOutput = [];

        internal RunningEngine(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        internal string StandardOutput => string.Join(Environment.NewLine, _standardOutput);

        /// <summary>
        ///     Returns the canonical readiness line, or <c>null</c> when the engine exited with
        ///     <see cref="PortInUseExitCode" /> before printing it — the one pre-readiness exit the caller
        ///     may retry on a fresh port. Every other pre-readiness exit throws with the child's stderr.
        /// </summary>
        internal async Task<string?> ReadReadyLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                _standardOutput.Add(line);
                if (line.StartsWith("XE_READY=1 ", StringComparison.Ordinal))
                {
                    return line;
                }
            }

            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (_process.ExitCode == PortInUseExitCode)
            {
                return null;
            }

            throw new InvalidOperationException(
                $"The engine exited with code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)} before readiness. "
                + $"stderr: {await _standardError.ConfigureAwait(false)}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync().ConfigureAwait(false);
            _ = await _standardError.ConfigureAwait(false);
            _process.Dispose();
        }
    }
}
