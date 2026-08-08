namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
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
        var probe = new ProcessGpuVendorProbe(() => true,
            TimeSpan.FromSeconds(8),
            (_, _) =>
            {
                shellAttempts++;
                throw new InvalidOperationException("The NVML fast path must not shell out to any tool.");
            });

        var vendor = await probe.DetectVendorAsync(CancellationToken.None);

        AssertEx.Equal(DetectedGpuVendor.Nvidia, vendor);
        AssertEx.Equal(expected: 0, shellAttempts, "the NVML fast path must not shell out to any tool");
    }

    [Test]
    public async Task WhenProbeOverruns_KillsProcess_AndReturnsUndetected()
    {
        // NVML absent → the probe falls through to the shelling path. The faked tool hangs forever, so the per-tool
        // timeout must fire, kill the child, and the overall detection must degrade to None (CPU floor) — no orphan.
        // The test owns the fake's lifetime; the probe is responsible for killing+disposing it on the timeout path.
        using var hangingProcess = new HangingProbeProcess();
        var shellAttempts = 0;
        var probe = new ProcessGpuVendorProbe(() => false,
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

    /// <summary>
    ///     The defect this closes: the Windows adapter list came only from <c>wmic</c>, a deprecated Feature-on-Demand
    ///     that is NOT installed by default on current Windows 11. Its absence was swallowed into "no adapter list",
    ///     which collapsed the vendor to <c>None</c> and selected <c>GpuVariant.Cpu</c> — so a Vulkan-capable AMD or
    ///     Intel box ran inference on the CPU, at a fraction of the speed, and told the user nothing.
    ///     <para>
    ///         The platform is an injected parameter rather than an <c>OperatingSystem.IsWindows()</c> call inside the
    ///         branch, so the branch that was wrong is the branch this Linux host actually runs.
    ///     </para>
    /// </summary>
    [Test]
    public async Task OnWindowsWithoutWmic_TheCimQueryStillDetectsTheAdapterVendor()
    {
        var probe = new ProcessGpuVendorProbe(() => false,
            TimeSpan.FromSeconds(8),
            ScriptedProcess.Factory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["nvidia-smi"] = null, // no NVIDIA driver tooling on this box
                ["powershell"] = "AMD Radeon RX 7800 XT\n",
                ["wmic"] = null // the Feature-on-Demand is not installed
            }),
            ProcessGpuVendorProbe.ProbePlatform.Windows);

        AssertEx.Equal(DetectedGpuVendor.Amd, await probe.DetectVendorAsync(CancellationToken.None));
    }

    [Test]
    public async Task OnWindows_WhenTheCimQueryAnswersNothing_FallsBackToWmic()
    {
        var attempted = new List<string>();
        var probe = new ProcessGpuVendorProbe(() => false,
            TimeSpan.FromSeconds(8),
            ScriptedProcess.Factory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nvidia-smi"] = null,
                    ["powershell"] = null,
                    ["wmic"] = "Name\nIntel(R) UHD Graphics 770\n"
                },
                attempted),
            ProcessGpuVendorProbe.ProbePlatform.Windows);

        AssertEx.Equal(DetectedGpuVendor.Intel, await probe.DetectVendorAsync(CancellationToken.None));

        var adapterAttempts = attempted.Where(name => !name.Contains("nvidia-smi", StringComparison.OrdinalIgnoreCase)).ToList();
        AssertEx.Contains(adapterAttempts[0], "powershell", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(adapterAttempts[^1], "wmic", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     NVIDIA answers from NVML or <c>nvidia-smi</c> long before the adapter list, so nothing on the Windows
    ///     adapter path can change what an NVIDIA box selects. Pinned because that is the regression this change is
    ///     most able to cause.
    /// </summary>
    [Test]
    public async Task OnWindows_NvidiaStillAnswersBeforeAnyAdapterQueryIsAttempted()
    {
        var attempted = new List<string>();
        var probe = new ProcessGpuVendorProbe(() => false,
            TimeSpan.FromSeconds(8),
            ScriptedProcess.Factory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nvidia-smi"] = "NVIDIA GeForce RTX 4080\n"
                },
                attempted),
            ProcessGpuVendorProbe.ProbePlatform.Windows);

        AssertEx.Equal(DetectedGpuVendor.Nvidia, await probe.DetectVendorAsync(CancellationToken.None));
        AssertEx.False(attempted.Any(name => name.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                                             || name.Contains("wmic", StringComparison.OrdinalIgnoreCase)),
            "an NVIDIA host must never pay for the adapter enumeration");
    }

    [Test]
    public void TheWindowsAdapterCandidatesPreferTheAbsoluteCimPathAndKeepWmicLast()
    {
        var candidates = ProcessGpuVendorProbe.WindowsAdapterListCommands(@"C:\Windows\system32").ToList();

        AssertEx.Contains(candidates[0].FileName, "powershell.exe");
        AssertEx.Contains(candidates[0].FileName, "system32", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(candidates[0].Arguments, "Win32_VideoController");
        AssertEx.Contains(candidates[0].Arguments, "-NoProfile");
        AssertEx.Contains(candidates[^1].FileName, "wmic");

        // A host that cannot report a system directory still gets the bare-name candidate rather than nothing.
        AssertEx.False(ProcessGpuVendorProbe.WindowsAdapterListCommands(systemDirectory: null)
                                            .Any(candidate => candidate.FileName.Contains("system32", StringComparison.OrdinalIgnoreCase)));
        AssertEx.Contains(ProcessGpuVendorProbe.WindowsAdapterListCommands(systemDirectory: null).First().FileName, "powershell");
    }

    [Test]
    public async Task OnLinux_TheAdapterListStillComesFromLspci()
    {
        var attempted = new List<string>();
        var probe = new ProcessGpuVendorProbe(() => false,
            TimeSpan.FromSeconds(8),
            ScriptedProcess.Factory(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["lspci"] = "01:00.0 VGA compatible controller: Advanced Micro Devices, Inc. [AMD/ATI] Navi 32\n"
                },
                attempted),
            ProcessGpuVendorProbe.ProbePlatform.Linux);

        AssertEx.Equal(DetectedGpuVendor.Amd, await probe.DetectVendorAsync(CancellationToken.None));
        AssertEx.False(attempted.Any(name => name.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                                             || name.Contains("wmic", StringComparison.OrdinalIgnoreCase)),
            "the Linux branch must not shell a Windows adapter query");
    }

    /// <summary>A fake probe process answering canned stdout per executable, recording which were attempted.</summary>
    private sealed class ScriptedProcess : ProcessGpuVendorProbe.IProbeProcess
    {
        private readonly string? _standardOutput;

        private ScriptedProcess(string? standardOutput)
        {
            _standardOutput = standardOutput;
        }

        public bool HasExited { get; private set; }

        public int ExitCode => _standardOutput is null ? 1 : 0;

        public static Func<string, string, ProcessGpuVendorProbe.IProbeProcess> Factory(IReadOnlyDictionary<string, string?> outputs,
            List<string>? attempted = null)
        {
            return (fileName, _) =>
            {
                attempted?.Add(fileName);
                var match = outputs.FirstOrDefault(entry => fileName.Contains(entry.Key, StringComparison.OrdinalIgnoreCase));
                return new ScriptedProcess(match.Value);
            };
        }

        public bool Start()
        {
            return true;
        }

        public Task<string> ReadStandardOutputAsync(CancellationToken ct)
        {
            return Task.FromResult(_standardOutput ?? string.Empty);
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            HasExited = true;
            return Task.CompletedTask;
        }

        public void Kill()
        {
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A fake probe process whose stdout read never completes until cancelled — simulating a wedged tool.</summary>
    private sealed class HangingProbeProcess : ProcessGpuVendorProbe.IProbeProcess
    {
        public bool WasKilled { get; private set; }

        public bool WasDisposed { get; private set; }

        public bool HasExited { get; private set; }

        public int ExitCode => 0;

        public bool Start()
        {
            return true;
        }

        public async Task<string> ReadStandardOutputAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return string.Empty;
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            return Task.Delay(Timeout.Infinite, ct);
        }

        public void Kill()
        {
            WasKilled = true;
            HasExited = true;
        }

        public void Dispose()
        {
            WasDisposed = true;
        }
    }
}
