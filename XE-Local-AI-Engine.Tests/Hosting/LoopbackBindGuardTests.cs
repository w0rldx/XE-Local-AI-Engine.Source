namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the loopback-only bind classifier (MED-001 startup guard). The wiring stops the app on a routable
///     bind; here the pure classifier is exercised directly so the "rejects a routable bind" decision is asserted without
///     spinning a real externally-bound listener.
/// </summary>
public sealed class LoopbackBindGuardTests
{
    [Test]
    public void FindNonLoopbackAddresses_WhenAllLoopback_ReturnsEmpty()
    {
        var addresses = new[]
        {
            "http://127.0.0.1:5000",
            "http://localhost:5001",
            "https://localhost:5002",
            "http://[::1]:5003"
        };

        var result = LoopbackBindGuard.FindNonLoopbackAddresses(addresses);

        AssertEx.Empty(result);
    }

    [Test]
    public void FindNonLoopbackAddresses_FlagsRoutableAndWildcardBinds()
    {
        var addresses = new[]
        {
            "http://127.0.0.1:5000",   // loopback, kept out of the result
            "http://0.0.0.0:5000",     // wildcard
            "http://192.168.1.10:5000", // routable LAN address
            "http://+:5000",           // Kestrel wildcard
            "http://*:5000"            // Kestrel wildcard
        };

        var result = LoopbackBindGuard.FindNonLoopbackAddresses(addresses);

        AssertEx.Equal(4, result.Count);
        AssertEx.False(result.Contains("http://127.0.0.1:5000"), "The loopback bind must not be flagged.");
    }

    // These two exercise the guarded-shutdown decision, which mutates the process-global Environment.ExitCode; the shared
    // NotInParallel key keeps them from racing each other on it, and each restores the original value.
    [Test]
    [NotInParallel("XE_LOOPBACK_GUARD_EXITCODE")]
    public void ShutDownIfBindIsRoutable_OnRoutableBind_SetsNonZeroExitCodeAndStops()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            using var lifetime = new StubHostApplicationLifetime();

            var shutDown = LoopbackBindGuard.ShutDownIfBindIsRoutable(
                new[] { "http://0.0.0.0:5000" }, lifetime, NullLogger.Instance);

            AssertEx.True(shutDown, "A routable bind must trigger a guarded shutdown.");
            AssertEx.Equal(1, lifetime.StopApplicationCallCount);
            AssertEx.Equal(1, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    [Test]
    [NotInParallel("XE_LOOPBACK_GUARD_EXITCODE")]
    public void ShutDownIfBindIsRoutable_OnLoopbackBind_LeavesExitCodeZeroAndDoesNotStop()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            using var lifetime = new StubHostApplicationLifetime();

            var shutDown = LoopbackBindGuard.ShutDownIfBindIsRoutable(
                new[] { "http://127.0.0.1:5000", "http://localhost:5001" }, lifetime, NullLogger.Instance);

            AssertEx.False(shutDown, "A loopback-only bind must not trigger a shutdown.");
            AssertEx.Equal(0, lifetime.StopApplicationCallCount);
            AssertEx.Equal(0, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = originalExitCode;
        }
    }

    // Minimal IHostApplicationLifetime stand-in: records StopApplication calls without a real host.
    private sealed class StubHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public int StopApplicationCallCount { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopApplicationCallCount++;
        }

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }
}
