namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the self-contained desktop launcher host hooks. Pure tests only: the
///     desktop-mode gate, browser-command builder, loopback URL resolver, the signal→StopApplication seam, and the
///     non-fatal browser-launch path. No real process, signal, or network is exercised.
/// </summary>
public sealed class DesktopLaunchTests
{
    [Test]
    public void DesktopModeGate_WhenFlagUnset_LeavesPipelineUnchanged()
    {
        // No CLI arg, no env signal → desktop mode is off, so Program.cs keeps the standard HTTPS/HSTS pipeline.
        var launchMode = DesktopLaunch.ResolveLaunchMode([], static _ => null, isManagedInstall: false);

        AssertEx.Equal(LaunchMode.Headless, launchMode);
    }

    [Test]
    public void DesktopModeGate_WhenEnvOrArgSet_EnablesDesktopPath()
    {
        var viaEnv = DesktopLaunch.ResolveLaunchMode([],
            static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "desktop" : null,
            isManagedInstall: false);

        var viaEnvCaseInsensitive = DesktopLaunch.ResolveLaunchMode([],
            static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "DESKTOP" : null,
            isManagedInstall: false);

        var viaArg = DesktopLaunch.ResolveLaunchMode(["--desktop"], static _ => null, isManagedInstall: false);

        AssertEx.Equal(LaunchMode.Desktop, viaEnv);
        AssertEx.Equal(LaunchMode.Desktop, viaEnvCaseInsensitive);
        AssertEx.Equal(LaunchMode.Desktop, viaArg);
    }

    [Test]
    public void DesktopModeGate_WhenVelopackManagedInstall_EnablesDesktopPathWithoutEnvOrArg()
    {
        // The Velopack stub launches the bare exe with neither the env var nor the --desktop arg, so the managed-install
        // signal alone must enable desktop mode — otherwise the packaged build never derives the node-sqlite connection
        // string and crashes applying migrations at startup.
        var launchMode = DesktopLaunch.ResolveLaunchMode([], static _ => null, isManagedInstall: true);

        AssertEx.Equal(LaunchMode.Desktop, launchMode);
    }

    [Test]
    public void ResolveLaunchMode_ExplicitMcpOnlyBeatsDesktopEnvironmentAndManagedInstall()
    {
        var mode = DesktopLaunch.ResolveLaunchMode(["--mcp-only"], static _ => "desktop", isManagedInstall: true);

        AssertEx.Equal(LaunchMode.McpOnly, mode);
        AssertEx.True(mode.IsLocalMode());
        AssertEx.True(DesktopLaunch.ShouldSuppressBrowser(mode, noBrowserRequested: false));
        AssertEx.False(DesktopLaunch.ShouldSuppressBrowser(LaunchMode.Desktop, noBrowserRequested: false));
    }

    [Test]
    public void CliParsers_ValidatePortSetupAndMcpScope()
    {
        AssertEx.True(DesktopLaunch.TryGetPort(["--port=41234"], out var port, out var portError));
        AssertEx.Equal(41234, port);
        AssertEx.Null(portError);
        AssertEx.False(DesktopLaunch.TryGetPort(["--port", "0"], out _, out _));
        AssertEx.True(DesktopLaunch.HasNoBrowserFlag(["--NO-BROWSER"]));

        var setupRequested = DesktopLaunch.TryGetSetupCommand(["--setup", "--admin-email=agent@example.test", "--admin-password", "StrongPass123!"],
            static _ => null,
            static () => null,
            out var setup,
            out var setupError);
        AssertEx.True(setupRequested);
        AssertEx.Null(setupError);
        AssertEx.Equal("agent@example.test", AssertEx.NotNull(setup).Email);

        AssertEx.True(DesktopLaunch.TryGetMcpKeyScope(["--mcp-key=agentic"], out var scope, out var scopeError));
        AssertEx.Equal(McpServerApiKeyScope.Agentic, scope);
        AssertEx.Null(scopeError);
    }

    [Test]
    public void SetupParser_MalformedExplicitEmailNeverFallsBackToEnvironment()
    {
        foreach (var args in new[]
                 {
                     new[]
                     {
                         "--setup",
                         "--admin-email=",
                         "--admin-password",
                         "StrongPass123!"
                     },
                     new[]
                     {
                         "--setup",
                         "--admin-email",
                         "--admin-password",
                         "StrongPass123!"
                     }
                 })
        {
            var requested = DesktopLaunch.TryGetSetupCommand(args,
                static name => name == DesktopLaunch.AdminEmailEnvironmentVariable ? "fallback@example.test" : null,
                static () => null,
                out var command,
                out var error);

            AssertEx.True(requested);
            AssertEx.Null(command);
            AssertEx.Contains(AssertEx.NotNull(error), DesktopLaunch.AdminEmailArgument);
        }
    }

    [Test]
    public void BuildRestartArguments_RetainsOnlyStableValidatedServeOptions()
    {
        var restartArgs = DesktopLaunch.BuildRestartArguments(
            ["--setup", "--admin-email", "admin@example.test", "--admin-password", "secret", "--mcp-key", "agentic", "--no-browser", "--port", "41234"],
            LaunchMode.McpOnly,
            port: 41234);

        AssertEx.True(restartArgs.SequenceEqual(["--mcp-only", "--no-browser", "--port", "41234"], StringComparer.Ordinal));
        AssertEx.False(restartArgs.Any(static value => value.Contains("secret", StringComparison.Ordinal)));
        AssertEx.False(restartArgs.Any(static value => value is "--setup" or "--mcp-key" or "--admin-email" or "--admin-password"));
    }

    [Test]
    public void ResetAdminPassword_WhenFlagAbsent_IsNotRequested()
    {
        var requested = DesktopLaunch.TryGetResetAdminPassword(["--desktop"], out var password);

        AssertEx.False(requested, "Without the flag the reset path must not run; the web host starts normally.");
        AssertEx.Null(password);
    }

    [Test]
    public void ResetAdminPassword_ReadsValueFromEitherSpaceOrEqualsForm()
    {
        var viaSpace = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password", "N3w!Passw0rd123"], out var spaceValue);
        var viaEquals = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password=N3w!Passw0rd123"], out var equalsValue);

        AssertEx.True(viaSpace);
        AssertEx.Equal("N3w!Passw0rd123", spaceValue);
        AssertEx.True(viaEquals, "The =form must be recognized so it is never mistaken for a normal launch.");
        AssertEx.Equal("N3w!Passw0rd123", equalsValue);
    }

    [Test]
    public void ResetAdminPassword_WhenFlagHasNoValue_IsRequestedButPasswordless()
    {
        // Flag present but no password supplied (trailing flag, or bare =): requested=true so Program.cs prints a usage
        // error and exits rather than silently booting the server with a half-typed command.
        var trailing = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password"], out var trailingValue);
        var bareEquals = DesktopLaunch.TryGetResetAdminPassword(["--reset-admin-password="], out var bareEqualsValue);

        AssertEx.True(trailing);
        AssertEx.Null(trailingValue);
        AssertEx.True(bareEquals);
        AssertEx.Null(bareEqualsValue);
    }

    [Test]
    public void KnowledgeDowngradeCommand_RecognizesPreflightAndExport()
    {
        AssertEx.Equal(KnowledgeDowngradeCommand.None, DesktopLaunch.GetKnowledgeDowngradeCommand(["--desktop"]));
        AssertEx.Equal(KnowledgeDowngradeCommand.Preflight,
            DesktopLaunch.GetKnowledgeDowngradeCommand(["--knowledge-downgrade-preflight"]));
        AssertEx.Equal(KnowledgeDowngradeCommand.Export,
            DesktopLaunch.GetKnowledgeDowngradeCommand(["--knowledge-downgrade-export"]));
    }

    [Test]
    public void KnowledgeDowngradeCommand_WhenBothFlagsArePresent_RejectsAmbiguousAction()
    {
        _ = AssertEx.Throws<ArgumentException>(() => DesktopLaunch.GetKnowledgeDowngradeCommand(["--knowledge-downgrade-preflight", "--knowledge-downgrade-export"]));
    }

    [Test]
    public void BrowserLaunchCommand_PerOs_UsesExplorerOrXdgOpen()
    {
        const string url = "http://127.0.0.1:5001/";

        var (windowsFile, windowsArgs) = BrowserLauncher.BuildOpenCommand(url, isWindows: true);
        var (linuxFile, linuxArgs) = BrowserLauncher.BuildOpenCommand(url, isWindows: false);

        AssertEx.Equal("explorer", windowsFile);
        AssertEx.Equal(url, windowsArgs);
        AssertEx.Equal("xdg-open", linuxFile);
        AssertEx.Equal(url, linuxArgs);

        // The launch must never shell-execute (repo convention guarded by CodexAuthServiceTests). Assert the intent
        // through the ProcessStartInfo the launcher constructs; do NOT assert on process exit code (explorer returns 1
        // on success).
        ProcessStartInfo? captured = null;
        BrowserLauncher.OpenBrowser(url, isWindows: true, NullLogger.Instance, startInfo => captured = startInfo);

        var startInfo = AssertEx.NotNull(captured);
        AssertEx.False(startInfo.UseShellExecute, "Browser launch must keep UseShellExecute=false.");
        AssertEx.Equal("explorer", startInfo.FileName);
    }

    [Test]
    public void LoopbackUrlResolver_ParsesServerAddressesFeature()
    {
        var feature = new FakeServerAddressesFeature();
        feature.Addresses.Add("http://127.0.0.1:54321");

        var resolved = LoopbackUrlResolver.Resolve(feature.Addresses);

        AssertEx.Equal("http://127.0.0.1:54321/", AssertEx.NotNull(resolved));
    }

    [Test]
    public void LoopbackUrlResolver_RewritesWildcardHostToLoopback()
    {
        // Kestrel can report a wildcard host for the bound listener; the resolver targets the loopback interface.
        var resolved = LoopbackUrlResolver.Resolve(["http://0.0.0.0:5000"]);

        AssertEx.Equal("http://127.0.0.1:5000/", AssertEx.NotNull(resolved));
    }

    [Test]
    public void DesktopLifecycle_SignalTrigger_CallsStopApplication()
    {
        using var lifetime = new FakeHostApplicationLifetime();
        using var server = new FakeServer(new FakeServerAddressesFeature());
        using var lifecycle = new DesktopLifecycle(lifetime, server, NullLogger<DesktopLifecycle>.Instance);

        // Drive the graceful-stop seam directly — no real SIGHUP / console-ctrl event.
        lifecycle.TriggerGracefulStop();

        AssertEx.Equal(expected: 1, lifetime.StopApplicationCallCount);
    }

    [Test]
    public void BrowserLaunch_WhenProcessStartThrows_IsNonFatal()
    {
        const string url = "http://127.0.0.1:5005/";

        // The launch action throws (e.g. xdg-open absent); OpenBrowser must swallow it and not propagate.
        BrowserLauncher.OpenBrowser(url, isWindows: false, NullLogger.Instance,
            static _ => throw new InvalidOperationException("xdg-open not found"));

        // Reaching here without an exception is the assertion: a failed launch does not abort startup.
        AssertEx.True(true);
    }

    private sealed class FakeServerAddressesFeature : IServerAddressesFeature
    {
        public ICollection<string> Addresses { get; } = new List<string>();

        public bool PreferHostingUrls { get; set; }
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(IServerAddressesFeature addresses)
        {
            var features = new FeatureCollection();
            features.Set(addresses);
            Features = features;
        }

        public IFeatureCollection Features { get; }

        public void Dispose()
        {
            // No unmanaged state; the interface requires IDisposable.
        }

        public Task StartAsync<TContext>(IHttpApplication<TContext> application,
            CancellationToken cancellationToken)
            where TContext : notnull
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopped = new();
        private readonly CancellationTokenSource _stopping = new();

        public int StopApplicationCallCount { get; private set; }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopApplicationCallCount++;
        }
    }
}
