namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The default GPU-vendor probe: a non-shelling NVML driver-presence fast path that detects NVIDIA without spawning
///     a process, and a reaping contract that guarantees an overrunning probe child is killed (no orphan) while
///     degrading to "undetected" — never blocking past the per-tool timeout. Exercised on any host via the injectable
///     driver-signal + process-factory seams (no real GPU / no real shelling).
/// </summary>
public sealed class ProcessGpuVendorProbeTests
{
    [Test]
    public async Task WhenNvmlPresent_DetectsNvidia_WithoutShelling()
    {
        var shellAttempts = 0;
        // NVML driver-presence signal is "present" — the probe must short-circuit to NVIDIA and never spawn a tool.
        // The factory throws if invoked so any shelling attempt fails the test loudly.
        var probe = new ProcessGpuVendorProbe(
            () => true,
            TimeSpan.FromSeconds(8),
            (_, _) =>
            {
                shellAttempts++;
                throw new InvalidOperationException("The NVML fast path must not shell out to any tool.");
            });

        var vendor = await probe.DetectVendorAsync(CancellationToken.None);

        AssertEx.Equal(DetectedGpuVendor.Nvidia, vendor);
        AssertEx.Equal(0, shellAttempts, "the NVML fast path must not shell out to any tool");
    }

    [Test]
    public async Task WhenProbeOverruns_KillsProcess_AndReturnsUndetected()
    {
        // NVML absent → the probe falls through to the shelling path. The faked tool hangs forever, so the per-tool
        // timeout must fire, kill the child, and the overall detection must degrade to None (CPU floor) — no orphan.
        // The test owns the fake's lifetime; the probe is responsible for killing+disposing it on the timeout path.
        using var hangingProcess = new HangingProbeProcess();
        var shellAttempts = 0;
        var probe = new ProcessGpuVendorProbe(
            () => false,
            TimeSpan.FromMilliseconds(20),
            (_, _) =>
            {
                shellAttempts++;
                return hangingProcess;
            });

        var vendor = await probe.DetectVendorAsync(CancellationToken.None);

        AssertEx.Equal(DetectedGpuVendor.None, vendor);
        AssertEx.True(shellAttempts > 0, "the probe must have attempted to shell a tool when NVML was absent");
        AssertEx.True(hangingProcess.WasKilled, "the overrunning child must be killed — no orphan may survive");
        AssertEx.True(hangingProcess.WasDisposed, "the child must be disposed on the timeout path");
    }

    /// <summary>A fake probe process whose stdout read never completes until cancelled — simulating a wedged tool.</summary>
    private sealed class HangingProbeProcess : ProcessGpuVendorProbe.IProbeProcess
    {
        public bool WasKilled { get; private set; }

        public bool WasDisposed { get; private set; }

        public bool HasExited { get; private set; }

        public int ExitCode => 0;

        public bool Start() => true;

        public async Task<string> ReadStandardOutputAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return string.Empty;
        }

        public Task WaitForExitAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

        public void Kill()
        {
            WasKilled = true;
            HasExited = true;
        }

        public void Dispose() => WasDisposed = true;
    }
}
