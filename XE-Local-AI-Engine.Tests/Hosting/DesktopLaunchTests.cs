namespace XE_Local_AI_Engine.Tests.Hosting;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
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
        var isDesktop = DesktopLaunch.IsDesktopMode(args: [], environmentReader: static _ => null);

        AssertEx.False(isDesktop);
    }

    [Test]
    public void DesktopModeGate_WhenEnvOrArgSet_EnablesDesktopPath()
    {
        var viaEnv = DesktopLaunch.IsDesktopMode(
            args: [],
            environmentReader: static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "desktop" : null);

        var viaEnvCaseInsensitive = DesktopLaunch.IsDesktopMode(
            args: [],
            environmentReader: static name => name == DesktopLaunch.LaunchModeEnvironmentVariable ? "DESKTOP" : null);

        var viaArg = DesktopLaunch.IsDesktopMode(args: ["--desktop"], environmentReader: static _ => null);

        AssertEx.True(viaEnv, "XE_LAUNCH_MODE=desktop must enable desktop mode.");
        AssertEx.True(viaEnvCaseInsensitive, "XE_LAUNCH_MODE is matched case-insensitively.");
        AssertEx.True(viaArg, "--desktop must enable desktop mode.");
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

        AssertEx.Equal(1, lifetime.StopApplicationCallCount);
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

        public System.Threading.Tasks.Task StartAsync<TContext>(
            Microsoft.AspNetCore.Hosting.Server.IHttpApplication<TContext> application,
            CancellationToken cancellationToken)
            where TContext : notnull => System.Threading.Tasks.Task.CompletedTask;

        public System.Threading.Tasks.Task StopAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public int StopApplicationCallCount { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => StopApplicationCallCount++;

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
