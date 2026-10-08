namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[NotInParallel]
[Category(TestCategories.Integration)]
public sealed class EngineCliProcessTests : IDisposable
{
    /// <summary>The engine's deterministic "--port is taken" exit code; it never falls back to another port.</summary>
    private const int PortInUseExitCode = 6;

    /// <summary>The node-settings file name owned by <c>NodeSettingsStore</c>, which keeps it private.</summary>
    private const string NodeSettingsFileName = "node-settings.json";

    /// <summary>The admin password every child gets in XE_ADMIN_PASSWORD unless a test withholds it.</summary>
    private const string AdminPassword = "!Demo1234567";

    private const string ReadyPrefix = "XE_READY=1 ";

    /// <summary>The node name every child runs under unless a test changes it; it is part of the node key derivation.</summary>
    private const string DefaultNodeName = "engine-cli-test";

    private static readonly TimeSpan HandOverDeadline = TimeSpan.FromSeconds(60);

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

        var beforeSetup = await RunAsync(["--mcp-key", "delegate"], launchMode: null);
        AssertEx.Equal(expected: 5, beforeSetup.ExitCode, beforeSetup.CombinedOutput);
        AssertEx.False(beforeSetup.StandardOutput.Contains("XE_MCP_KEY=", StringComparison.Ordinal));

        var firstSetup = await RunAsync(["--setup"], DesktopLaunch.McpOnlyModeValue);
        AssertEx.Equal(expected: 0, firstSetup.ExitCode, firstSetup.CombinedOutput);
        AssertEx.Contains(firstSetup.StandardOutput, "XE_SETUP=created");
        AssertEx.Contains(firstSetup.StandardOutput, "XE_ADMIN_EMAIL=agent@example.test");
        AssertEx.False(File.Exists(Path.Combine(_root, DesktopPortStore.ReadyFileName)),
            "An environment-only launch mode must not turn setup into a serving process.");

        var secondSetup = await RunAsync(["--setup"], launchMode: null);
        AssertEx.Equal(expected: 0, secondSetup.ExitCode, secondSetup.CombinedOutput);
        AssertEx.Contains(secondSetup.StandardOutput, "XE_SETUP=already-configured");
        AssertEx.False(secondSetup.StandardOutput.Contains("XE_ADMIN_EMAIL=", StringComparison.Ordinal));

        var agentic = await RunAsync(["--mcp-key=agentic"], launchMode: null);
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
            var line = await started.ReadReadyLineAsync();
            if (line is null)
            {
                await started.DisposeAsync();
                return null;
            }

            return new ServingEngine
            {
                Engine = started,
                Port = candidate,
                ReadyLine = line
            };
        });

        await using var engine = serving.Engine;
        var port = serving.Port;
        var readyLine = serving.ReadyLine;
        var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root));

        AssertEx.Equal($"XE_READY=1 XE_VERSION={ready.Version} XE_URL={ready.Url} XE_MCP_URL={ready.McpUrl} XE_DATA_DIR={ready.DataDir}", readyLine);
        AssertEx.Equal($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}", ready.Url);
        AssertEx.Equal($"{ready.Url}/api/local/v1/mcp/server", ready.McpUrl);
        AssertEx.Equal(_root, ready.DataDir);
        AssertEx.True(engine.StandardOutput.Contains("XE_SETUP=created", StringComparison.Ordinal));
        var status = await RunAsync(["--status", "--json"], launchMode: null);
        AssertEx.Equal(expected: 0, status.ExitCode, status.CombinedOutput);
        using (var document = JsonDocument.Parse(status.StandardOutput))
        {
            AssertEx.True(document.RootElement.GetProperty("running").GetBoolean());
            AssertEx.False(document.RootElement.GetProperty("setupRequired").GetBoolean());
            AssertEx.Equal(ready.Url, document.RootElement.GetProperty("url").GetString());
        }

        var leaseConflict = await RunAsync(["--mcp-key", "delegate"], launchMode: null);
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
            occupiedRoot);
        AssertEx.Equal(PortInUseExitCode, occupied.ExitCode, occupied.CombinedOutput);
        Directory.Delete(occupiedRoot, recursive: true);
    }

    [Test]
    public async Task MalformedExplicitAdminEmail_IsProcessLevelUsageFailure()
    {
        var result = await RunAsync(["--setup", "--admin-email="], launchMode: null);

        AssertEx.Equal(expected: 2, result.ExitCode, result.CombinedOutput);
        AssertEx.Contains(result.StandardError, "--admin-email");
    }

    [Test]
    public async Task Setup_WhenFilesystemPreparationFails_ReturnsRedactedExitOneInsteadOfRuntimeAbort()
    {
        var blockedDataPath = Path.Combine(Path.GetTempPath(), "xe-engine-cli-blocked-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blockedDataPath, "not-a-directory");
        try
        {
            var result = await RunAsync(["--setup"], launchMode: null, blockedDataPath);

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

    [Test]
    public async Task Setup_WhenNodeSettingsFileIsUnreadable_ReturnsExitFiveNamingTheRecovery()
    {
        // The store's UpdateAsync refuses a read-modify-write over a present-but-unreadable file, and NodeAuthService
        // stamps the pending external-access profile through it BEFORE the identity commit. The HTTP endpoint maps that
        // refusal to a 400; the CLI must report its documented exit code 5 with the operator-facing recovery, not die
        // through the top-level fatal handler.
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, NodeSettingsFileName), "{ \"maxMessageRequestTimeoutSeconds\": ");

        var result = await RunAsync(["--setup"], launchMode: null);

        AssertEx.Equal(expected: 5, result.ExitCode, result.CombinedOutput);
        AssertEx.Contains(result.StandardError, "could not be read, so nothing was written.");
        AssertEx.Contains(result.StandardError, "Repair or delete node-settings.json and try again.");
        AssertEx.False(result.StandardOutput.Contains("XE_SETUP=created", StringComparison.Ordinal),
            "Setup must not claim it created an administrator when the settings write that precedes the commit failed.");
    }

    [Test]
    public async Task LockedVault_ServesTheUnlockPage_UnlocksOnTheSameOrigin_AndOneShotsNeedTheSecret()
    {
        Directory.CreateDirectory(_root);
        await SetupLockedVaultAsync();

        var withoutPassword = await RunAsync(["--mcp-key", "agentic"], launchMode: null, adminPassword: null);
        AssertEx.Equal(expected: 5, withoutPassword.ExitCode, withoutPassword.CombinedOutput);
        AssertEx.Contains(withoutPassword.StandardError, DesktopLaunch.AdminPasswordEnvironmentVariable);
        // A downgrade command is a one-shot too: without the secret it must exit, never park on the unlock page.
        var downgradeWithoutPassword = await RunAsync(["--knowledge-downgrade-preflight"], DesktopLaunch.McpOnlyModeValue, adminPassword: null);
        AssertEx.Equal(expected: 5, downgradeWithoutPassword.ExitCode, downgradeWithoutPassword.CombinedOutput);
        AssertEx.False(downgradeWithoutPassword.StandardOutput.Contains(ReadyPrefix, StringComparison.Ordinal), downgradeWithoutPassword.CombinedOutput);
        var withPassword = await RunAsync(["--mcp-key", "agentic"], launchMode: null);
        AssertEx.Equal(expected: 0, withPassword.ExitCode, withPassword.CombinedOutput);
        AssertEx.Contains(withPassword.StandardOutput, "XE_MCP_KEY=xemcp_");

        var resetWithoutCode = await RunAsync(["--mcp-only", "--reset-admin-password", "another long password"], launchMode: null);
        AssertEx.Equal(expected: 5, resetWithoutCode.ExitCode, resetWithoutCode.CombinedOutput);
        AssertEx.Contains(resetWithoutCode.StandardError, DesktopLaunch.RecoveryCodeStdinArgument);

        await using var engine = StartServing(["--mcp-only"], adminPassword: null);
        AssertEx.NotNull(await engine.ReadReadyLineAsync(), "The locked engine must announce readiness from the pre-host.");
        engine.StartDraining();
        var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(ready.Url)
        };

        var status = await RunAsync(["--status", "--json"], launchMode: null);
        AssertEx.Equal(expected: 0, status.ExitCode, status.CombinedOutput);
        using (var document = JsonDocument.Parse(status.StandardOutput))
        {
            AssertEx.True(document.RootElement.GetProperty("running").GetBoolean());
            AssertEx.False(document.RootElement.GetProperty("setupRequired").GetBoolean());
            AssertEx.Equal(NodeAuthVaultStatus.Locked, document.RootElement.GetProperty("vault").GetString());
        }

        AssertEx.Equal(NodeAuthVaultStatus.Locked, (await client.GetFromJsonAsync<NodeAuthStatusResponse>(Route(LocalApiRoutes.Auth.Status)))!.Vault);
        using (var other = await client.GetAsync(Route(LocalApiRoutes.Auth.Login)))
        {
            AssertEx.Equal(HttpStatusCode.ServiceUnavailable, other.StatusCode);
        }

        using (var wrong = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = "not the admin password"
               }))
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using (var right = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = AdminPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        var unlocked = await WaitForRealHostAsync(engine, client);
        AssertEx.False(unlocked.SetupRequired);
        AssertEx.Equal(ready.Url, AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root)).Url);
        await AssertStatusVaultAsync(NodeAuthVaultStatus.Unlocked);
    }

    [Test]
    public async Task LockedVault_ServeWithTheAdminPassword_UnlocksWithoutThePreHost_AndAWrongOneExitsFive()
    {
        Directory.CreateDirectory(_root);
        await SetupLockedVaultAsync();

        var wrong = await RunAsync(["--mcp-only"], launchMode: null, adminPassword: "not the admin password");
        AssertEx.Equal(expected: 5, wrong.ExitCode, wrong.CombinedOutput);
        AssertEx.False(wrong.StandardOutput.Contains(ReadyPrefix, StringComparison.Ordinal), "A wrong password must never fall back to the unlock page.");

        var serving = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var started = StartServing(["--mcp-only", "--port", candidate.ToString(CultureInfo.InvariantCulture)]);
            if (await started.ReadReadyLineAsync() is null)
            {
                await started.DisposeAsync();
                return null;
            }

            return started;
        });
        await using var engine = serving;
        engine.StartDraining();
        var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(ready.Url)
        };

        var status = AssertEx.NotNull(await client.GetFromJsonAsync<NodeAuthStatusResponse>(Route(LocalApiRoutes.Auth.Status)));
        AssertEx.Equal(NodeAuthVaultStatus.Unlocked, status.Vault);
        AssertEx.Equal(expected: 1, engine.ReadyLineCount, "An env-password unlock must start the real host directly, never the unlock page.");
        await AssertStatusVaultAsync(NodeAuthVaultStatus.Unlocked);
    }

    private async Task AssertStatusVaultAsync(string expected)
    {
        var status = await RunAsync(["--status", "--json"], launchMode: null);
        AssertEx.Equal(expected: 0, status.ExitCode, status.CombinedOutput);
        using var document = JsonDocument.Parse(status.StandardOutput);
        AssertEx.Equal(expected, document.RootElement.GetProperty("vault").GetString());
    }

    [Test]
    public async Task LockedVault_RecoveryUnlock_ResetsTheAdminPasswordForTheRealHost()
    {
        Directory.CreateDirectory(_root);
        var recoveryCode = await SetupLockedVaultAsync();

        await using var engine = StartServing(["--mcp-only"], adminPassword: null);
        AssertEx.NotNull(await engine.ReadReadyLineAsync(), "The locked engine must announce readiness from the pre-host.");
        engine.StartDraining();
        var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root));
        using var client = new HttpClient
        {
            BaseAddress = new Uri(ready.Url)
        };

        const string newPassword = "A brand-new admin passw0rd";
        using (var recovered = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlockRecovery), new
               {
                   recoveryCode,
                   newPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        }

        await WaitForRealHostAsync(engine, client);
        using (var oldLogin = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.Login), new
               {
                   password = AdminPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode, engine.StandardOutput);
        }

        using (var newLogin = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.Login), new
               {
                   password = newPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.OK, newLogin.StatusCode, await newLogin.Content.ReadAsStringAsync());
        }
    }

    [Test]
    public async Task LockedVault_WhenTheUnlockPagePortIsTakenDuringTheHandOver_ExitsSevenInsteadOfMovingOrigin()
    {
        Directory.CreateDirectory(_root);
        await SetupLockedVaultAsync();
        await using var engine = StartServing(["--mcp-only"], adminPassword: null);
        AssertEx.NotNull(await engine.ReadReadyLineAsync(), "The locked engine must announce readiness from the pre-host.");
        engine.StartDraining();
        var ready = AssertEx.NotNull(await DesktopPortStore.ReadReadyAsync(_root));
        var port = new Uri(ready.Url).Port;
        using var client = new HttpClient
        {
            BaseAddress = new Uri(ready.Url)
        };

        // An unlock request whose body never finishes keeps the pre-host's graceful stop draining after Kestrel unbound the
        // port, which is the gap the test takes the port in. "100 Continue" proves the handler is already reading that body.
        using var parked = new TcpClient();
        await parked.ConnectAsync(IPAddress.Loopback, port);
        var stream = parked.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /{LocalApiRoutes.Prefix}/{LocalApiRoutes.Auth.VaultUnlock} HTTP/1.1\r\nHost: 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}\r\n"
                                                        + "Content-Type: application/json\r\nContent-Length: 1000\r\nExpect: 100-continue\r\n\r\n"));
        var interim = new byte[256];
        using (var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var read = await stream.ReadAsync(interim, readTimeout.Token);
            AssertEx.Contains(Encoding.ASCII.GetString(interim, 0, read), "100 Continue");
        }

        using (var right = await client.PostAsJsonAsync(Route(LocalApiRoutes.Auth.VaultUnlock), new
               {
                   password = AdminPassword
               }))
        {
            AssertEx.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        TcpListener? holder = null;
        try
        {
            await AssertEx.EventuallyAsync(() => TryHold(port, out holder),
                TimeSpan.FromSeconds(4),
                "The pre-host must release its port while its stop drains the parked request.");
            parked.Close();

            var exitCode = await engine.WaitForExitAsync(TimeSpan.FromSeconds(20));

            var standardError = await engine.StandardErrorAsync();
            AssertEx.Equal(expected: 7, exitCode, standardError);
            AssertEx.Contains(standardError, $"Port {port.ToString(CultureInfo.InvariantCulture)}");
            AssertEx.Contains(standardError, "Start the engine again.");
            AssertEx.Equal(1, engine.ReadyLineCount, "Only the pre-host may have announced readiness.");
        }
        finally
        {
            holder?.Dispose();
        }
    }

    [Test]
    public async Task Serve_WhenTheNodeKeyDoesNotMatchTheDatabase_ExitsEightInsteadOfCrashing()
    {
        Directory.CreateDirectory(_root);
        await SetupLockedVaultAsync();
        await using (var engine = StartServing(["--mcp-only"]))
        {
            // Readiness comes after the hosted services started, so the node-key check value is recorded by now.
            AssertEx.NotNull(await engine.ReadReadyLineAsync(), "The first serve must reach readiness and record the key check.");
        }

        // The same node.key under another node name derives another database key, which the recorded check refuses.
        var mismatched = await RunAsync(["--mcp-only"], launchMode: null, nodeName: "another-node-name");

        AssertEx.Equal(expected: 8, mismatched.ExitCode, mismatched.CombinedOutput);
        AssertEx.Contains(mismatched.StandardError, "does not match this database");
        AssertEx.Contains(mismatched.StandardError, $"move {DesktopBootstrap.DatabaseFileName} aside");
        AssertEx.False(mismatched.StandardOutput.Contains(ReadyPrefix, StringComparison.Ordinal), "A refused key must never announce readiness.");
    }

    private static bool TryHold(int port, out TcpListener? holder)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            holder = listener;
            return true;
        }
        catch (SocketException)
        {
            listener.Dispose();
            holder = null;
            return false;
        }
    }

    /// <summary>Runs <c>--setup</c> on the fresh data dir and returns the printed recovery code; node.key is then a v2 vault.</summary>
    private async Task<string> SetupLockedVaultAsync()
    {
        var setup = await RunAsync(["--setup"], DesktopLaunch.McpOnlyModeValue);
        AssertEx.Equal(expected: 0, setup.ExitCode, setup.CombinedOutput);
        var codeLine = setup.StandardOutput.Split(Environment.NewLine).SingleOrDefault(static line => line.StartsWith("XE_RECOVERY_CODE=", StringComparison.Ordinal));
        var recoveryCode = AssertEx.NotNull(codeLine, "--setup must print the recovery code once.")["XE_RECOVERY_CODE=".Length..];
        AssertEx.NotNullOrEmpty(recoveryCode);
        var keyFile = await File.ReadAllTextAsync(Path.Combine(_root, DesktopBootstrap.KeyFileName));
        AssertEx.True(keyFile.StartsWith('{'), "--setup must leave node.key as a v2 vault, never the raw secret.");
        return recoveryCode;
    }

    /// <summary>Waits for the real host's own readiness line, then for it to answer auth/status as unlocked on the same client origin.</summary>
    private static async Task<NodeAuthStatusResponse> WaitForRealHostAsync(RunningEngine engine, HttpClient client)
    {
        await AssertEx.EventuallyAsync(() => engine.ReadyLineCount >= 2 || engine.HasExited, HandOverDeadline,
            "The real host never announced readiness after the unlock.");
        AssertEx.False(engine.HasExited, $"The engine exited after the unlock: {engine.StandardOutput}");
        NodeAuthStatusResponse? status = null;
        await AssertEx.EventuallyAsync(async () =>
        {
            try
            {
                status = await client.GetFromJsonAsync<NodeAuthStatusResponse>(Route(LocalApiRoutes.Auth.Status));
                return status?.Vault == NodeAuthVaultStatus.Unlocked;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }, HandOverDeadline, "auth/status never reported the vault unlocked on the same origin.");
        return status!;
    }

    private static Uri Route(string route) =>
        new($"/{LocalApiRoutes.Prefix}/{route}", UriKind.Relative);

    private async Task<CommandResult> RunAsync(IReadOnlyList<string> arguments,
        string? launchMode,
        string? dataDirectory = null,
        string? adminPassword = AdminPassword,
        string nodeName = DefaultNodeName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = CreateStartInfo(arguments, launchMode, dataDirectory ?? _root, adminPassword, nodeName)
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The engine CLI process could not be started.");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return new CommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = await stdout,
            StandardError = await stderr
        };
    }

    private RunningEngine StartServing(IReadOnlyList<string> arguments, string? adminPassword = AdminPassword)
    {
        var process = new Process
        {
            StartInfo = CreateStartInfo(arguments, launchMode: null, _root, adminPassword)
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The engine serving process could not be started.");
        }

        return new RunningEngine(process);
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> arguments,
        string? launchMode,
        string dataDirectory,
        string? adminPassword = AdminPassword,
        string nodeName = DefaultNodeName)
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
        startInfo.Environment["WorkerNode__NodeName"] = nodeName;
        startInfo.Environment[DesktopBootstrap.DataDirectoryEnvironmentVariable] = dataDirectory;
        startInfo.Environment[DesktopLaunch.AdminEmailEnvironmentVariable] = "agent@example.test";
        if (adminPassword is not null)
        {
            startInfo.Environment[DesktopLaunch.AdminPasswordEnvironmentVariable] = adminPassword;
        }
        else
        {
            startInfo.Environment.Remove(DesktopLaunch.AdminPasswordEnvironmentVariable);
        }

        if (launchMode is not null)
        {
            startInfo.Environment[DesktopLaunch.LaunchModeEnvironmentVariable] = launchMode;
        }

        return startInfo;
    }

    private sealed record ServingEngine
    {
        public required RunningEngine Engine { get; init; }

        public required int Port { get; init; }

        public required string ReadyLine { get; init; }
    }

    private sealed record CommandResult
    {
        public required int ExitCode { get; init; }

        public required string StandardOutput { get; init; }

        public required string StandardError { get; init; }

        public string CombinedOutput => string.Concat(StandardOutput, Environment.NewLine, StandardError);
    }

    private sealed class RunningEngine : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private readonly List<string> _standardOutput = [];
        private Task _drain = Task.CompletedTask;

        internal RunningEngine(Process process)
        {
            _process = process;
            _standardError = process.StandardError.ReadToEndAsync();
        }

        internal string StandardOutput
        {
            get
            {
                lock (_standardOutput)
                {
                    return string.Join(Environment.NewLine, _standardOutput);
                }
            }
        }

        internal int ReadyLineCount
        {
            get
            {
                lock (_standardOutput)
                {
                    return _standardOutput.Count(static line => line.StartsWith(ReadyPrefix, StringComparison.Ordinal));
                }
            }
        }

        internal bool HasExited => _process.HasExited;

        internal async Task<int> WaitForExitAsync(TimeSpan timeout)
        {
            using var deadline = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(deadline.Token);
            await _drain;
            return _process.ExitCode;
        }

        internal Task<string> StandardErrorAsync() =>
            _standardError;

        /// <summary>
        ///     Keeps reading stdout after readiness, so a chatty child never blocks on a full pipe and a later readiness
        ///     line (the real host after a vault unlock) is observable.
        /// </summary>
        internal void StartDraining()
        {
            _drain = Task.Run(async () =>
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (_standardOutput)
                    {
                        _standardOutput.Add(line);
                    }
                }
            });
        }

        /// <summary>
        ///     Returns the canonical readiness line, or <c>null</c> when the engine exited with
        ///     <see cref="PortInUseExitCode" /> before printing it — the one pre-readiness exit the caller
        ///     may retry on a fresh port. Every other pre-readiness exit throws with the child's stderr.
        /// </summary>
        internal async Task<string?> ReadReadyLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                _standardOutput.Add(line);
                if (line.StartsWith("XE_READY=1 ", StringComparison.Ordinal))
                {
                    return line;
                }
            }

            await _process.WaitForExitAsync(timeout.Token);
            if (_process.ExitCode == PortInUseExitCode)
            {
                return null;
            }

            throw new InvalidOperationException($"The engine exited with code {_process.ExitCode.ToString(CultureInfo.InvariantCulture)} before readiness. "
                                                + $"stderr: {await _standardError}");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            await _process.WaitForExitAsync();
            _ = await _standardError;
            await _drain;
            _process.Dispose();
        }
    }
}
