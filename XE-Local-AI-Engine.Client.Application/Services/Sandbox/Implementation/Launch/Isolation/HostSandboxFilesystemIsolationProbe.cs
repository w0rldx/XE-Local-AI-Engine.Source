namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>The outcome of one filesystem-isolation measurement: the ingredients, or the reason there are none.</summary>
internal sealed record SandboxFilesystemIsolationProbeResult(SandboxFilesystemIsolation? Isolation, string? Reason);

/// <summary>
///     Measures whether this host can really run a command with the host filesystem absent from its mount namespace,
///     by RUNNING the production chain once against a throwaway jail and checking its positive controls.
///     <para>
///         Not a presence check, and not a simplified stand-in. <c>bwrap</c> can be installed and still fail — user
///         namespaces disabled by sysctl, an <c>AppArmor</c> profile that blocks unprivileged <c>userns</c>, a kernel
///         without <c>openat2</c>, a filesystem layout the usr-merge rule does not recognise, a jail whose ancestors
///         are writable by someone else. Every one of those produces a chain that fails at exec time, and advertising
///         a filesystem boundary on the strength of a file existing is the precise dishonesty this work removes.
///     </para>
///     <para>
///         The controls are POSITIVE as well as negative, which matters more than it sounds. "The canary is not
///         visible inside" is satisfied by a chain that failed to start at all, by a typo in the canary path, and by a
///         shell that never ran — so the same probe also requires that the workload wrote to <c>/work</c>, read
///         <c>/dev/urandom</c>, saw itself as pid 2, and found the synthetic <c>/etc/passwd</c>. A measurement that
///         can only fail open is not a measurement.
///     </para>
/// </summary>
internal static class HostSandboxFilesystemIsolationProbe
{
    // The chain does real work — a dbus round trip to the user manager, five namespaces, a dozen mounts — so it gets a
    // longer budget than the single-mechanism probes. Still short enough that a host which cannot do it at all does
    // not visibly delay startup.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(25);

    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    ///     Runs the measurement. Never throws: every failure becomes a result carrying the measured reason, which is
    ///     what the caller logs and what a live-gated test skips with.
    /// </summary>
    public static SandboxFilesystemIsolationProbeResult Measure(IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        ArgumentNullException.ThrowIfNull(userBusEnvironment);

        if (!OperatingSystem.IsLinux())
        {
            return new SandboxFilesystemIsolationProbeResult(Isolation: null, "the host is not Linux");
        }

        try
        {
            return MeasureCore(userBusEnvironment);
        }
        catch (SandboxIsolationUnavailableException exception)
        {
            return new SandboxFilesystemIsolationProbeResult(Isolation: null, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or SocketException)
        {
            return new SandboxFilesystemIsolationProbeResult(Isolation: null, $"the filesystem isolation probe failed: {exception.Message}");
        }
    }

    private static SandboxFilesystemIsolationProbeResult MeasureCore(IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        if (userBusEnvironment.Count == 0)
        {
            return Unavailable("neither XDG_RUNTIME_DIR nor DBUS_SESSION_BUS_ADDRESS is set, so the transient scope that is the kill authority cannot be created");
        }

        var setsid = TrustedBinaryResolver.Resolve("setsid");
        var systemdRun = TrustedBinaryResolver.Resolve("systemd-run");
        var systemctl = TrustedBinaryResolver.Resolve("systemctl");
        var bwrap = TrustedBinaryResolver.Resolve("bwrap");
        if (setsid is null || systemdRun is null || systemctl is null || bwrap is null)
        {
            var missing = new List<string>(capacity: 4);
            AddWhenMissing(missing, setsid, "setsid");
            AddWhenMissing(missing, systemdRun, "systemd-run");
            AddWhenMissing(missing, systemctl, "systemctl");
            AddWhenMissing(missing, bwrap, "bwrap");

            return Unavailable($"no root-owned {string.Join(", ", missing)} was found under {string.Join(", ", TrustedBinaryResolver.TrustedRoots)}");
        }

        // The inner assertions need a shell. It comes from the read-only /usr bind, so it is resolved by the same trust
        // rule as the helpers; bash is preferred only because its /dev/tcp redirection is what turns the egress check
        // into a real connect attempt rather than an inspection of /proc.
        var bash = TrustedBinaryResolver.Resolve("bash");
        var shell = bash ?? TrustedBinaryResolver.Resolve("sh");
        if (shell is null)
        {
            return Unavailable("no root-owned shell was found to run the probe's positive controls with");
        }

        var usrMerge = SandboxUsrMergeLayout.Resolve(SandboxUsrMergeLayout.Inspect);

        var isolation = new SandboxFilesystemIsolation
        {
            SetsidPath = setsid,
            SystemdRunPath = systemdRun,
            SystemctlPath = systemctl,
            BwrapPath = bwrap,
            UsrMergeEntries = usrMerge,
            UserId = geteuid(),
            GroupId = getegid(),
            UserBusEnvironment = userBusEnvironment
        };

        using var scratch = new ProbeScratch();
        using var listener = new HostLoopbackListener();
        if (!listener.CanBeReachedFromTheHost())
        {
            // The positive half of the egress control. Without it, "the connect failed inside" would also be satisfied
            // by a listener that was never reachable from anywhere.
            return Unavailable("the probe's own loopback listener could not be reached from the host, so egress denial could not be positively controlled");
        }

        var script = BuildProbeScript(scratch.HomeCanaryPath,
            scratch.SiblingCanaryPath,
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            listener.Port,
            bash);
        using var launch = SandboxIsolationLaunch.Create(isolation,
            new SandboxIsolationLaunchRequest
            {
                JailRoot = scratch.JailPath,
                Executable = shell,
                Arguments = ["-c", script],
                RuntimeMaxSeconds = (long)ProbeTimeout.TotalSeconds + 10,
                ThreadLimit = 1,
                Role = "probe"
            });

        var (exitCode, output) = RunChain(launch.Chain, userBusEnvironment);
        var facts = ParseFacts(output);

        var failure = FindFailedControl(facts, scratch, exitCode);

        return failure is null
            ? new SandboxFilesystemIsolationProbeResult(isolation, Reason: null)
            : Unavailable(failure);
    }

    /// <summary>
    ///     Checks every control and returns the first that did not hold, or <see langword="null" /> when all did. Each
    ///     branch names what was measured rather than "the probe failed", because this string is what a degraded host
    ///     logs and what a skipped live test prints.
    /// </summary>
    private static string? FindFailedControl(IReadOnlyDictionary<string, string> facts, ProbeScratch scratch, int exitCode)
    {
        if (exitCode != 0 || !facts.TryGetValue("DONE", out var done) || !string.Equals(done, "1", StringComparison.Ordinal))
        {
            return $"the isolated chain did not run to completion (exit {exitCode})";
        }

        // Positive controls first: they are what proves the chain really ran, so a negative control that "passed"
        // because nothing executed cannot be mistaken for containment.
        var positives = new (string Key, string Expected, string Failure)[]
        {
            ("PID", "2", "the workload was not pid 2 inside the sandbox, so the PID namespace was not created"),
            ("WORKWRITE", "OK", "the workload could not write to its own /work jail"),
            ("TMPWRITE", "OK", "the workload could not write to the jail-backed /tmp"),
            ("DEVNULL", "OK", "/dev/null was not usable inside the sandbox"),
            ("URANDOM", "OK", "/dev/urandom was not readable inside the sandbox"),
            ("PROCREAD", "OK", "/proc was not readable inside the sandbox"),
            ("PASSWD", "2", "the synthetic /etc/passwd did not contain exactly the two accounts the jail defines"),
            ("RUN", "YES", "/run did not exist inside the sandbox")
        };

        foreach (var (key, expected, failure) in positives)
        {
            if (!facts.TryGetValue(key, out var value) || !string.Equals(value, expected, StringComparison.Ordinal))
            {
                return failure;
            }
        }

        var negatives = new (string Key, string Expected, string Failure)[]
        {
            ("HOMECANARY", "ABSENT", "a canary file under the engine user's home was visible inside the sandbox"),
            ("SIBLINGCANARY", "ABSENT", "a canary file beside the jail — where other sandboxes live — was visible inside the sandbox"),
            ("HOSTPID", "INVISIBLE", "a host process was visible in the sandbox's /proc"),
            ("DEVCREATE", "EROFS", "a file could be created under /dev, so it was not remounted read-only"),
            ("PROCCREATE", "REFUSED", "a file could be created under /proc"),
            ("ROOTWRITE", "DENIED", "the sandbox root filesystem was writable"),
            ("USRWRITE", "DENIED", "the read-only /usr bind was writable"),
            ("ETCWRITE", "DENIED", "the synthetic /etc was writable"),
            ("RUNENTRIES", "0", "/run inside the sandbox was not empty"),
            ("BUS", "ABSENT", "the host's user bus socket path existed inside the sandbox"),
            ("DOCKER", "ABSENT", "a docker daemon socket path existed inside the sandbox"),
            ("LOOPBACK", "DENIED", "a loopback connect to a host listener succeeded from inside the sandbox")
        };

        foreach (var (key, expected, failure) in negatives)
        {
            if (!facts.TryGetValue(key, out var value) || !string.Equals(value, expected, StringComparison.Ordinal))
            {
                return failure;
            }
        }

        // The other half of each canary control: the file has to still be there afterwards, or "not visible inside"
        // proves nothing about the boundary and everything about the file having been deleted.
        return scratch.CanariesStillExist()
            ? null
            : "the probe's canary files did not survive the run, so their absence inside the sandbox proves nothing";
    }

    /// <summary>
    ///     The inner assertions, as one POSIX shell script printing <c>KEY=VALUE</c> lines. It is a script rather than
    ///     a series of commands because each control has to run inside the SAME sandbox instance — a fresh chain per
    ///     assertion would multiply the probe's cost by fifteen and would not be measuring one jail.
    ///     <para>
    ///         Every host PATH the script names is interpolated through <see cref="Quote" />, never pasted between
    ///         two apostrophes. Those paths are host data — a home directory, the sandbox container root, an
    ///         <c>XDG_RUNTIME_DIR</c> — and a single apostrophe anywhere in one of them closes the quote the script
    ///         text opened, which turns the remainder of that line into shell syntax the probe never intended and
    ///         makes it report a perfectly capable host unable to isolate.
    ///     </para>
    ///     <para>
    ///         The paths are taken as parameters rather than read from <see cref="ProbeScratch" /> and the environment
    ///         so that the rendering — the part that has to survive a hostile path — can be asserted without creating
    ///         a jail or running a chain.
    ///     </para>
    /// </summary>
    internal static string BuildProbeScript(string homeCanaryPath,
        string siblingCanaryPath,
        string? runtimeDirectory,
        int listenerPort,
        string? bash)
    {
        var busPath = Quote(runtimeDirectory is { Length: > 0 } directory
            ? $"{directory}/bus"
            : "/run/user/0/bus");

        var loopback = bash is null
            // Without bash there is no way to attempt a connect from a shell, so the control falls back to proving the
            // network namespace is empty: a fresh netns has no sockets at all, so /proc/net/tcp holds only its header.
            ? "if [ \"$(wc -l < /proc/net/tcp)\" = \"1\" ]; then echo LOOPBACK=DENIED; else echo LOOPBACK=REACHED; fi"
            : string.Create(CultureInfo.InvariantCulture,
                $"if {Quote(bash)} -c 'exec 3<>/dev/tcp/127.0.0.1/{listenerPort}' 2>/dev/null; then echo LOOPBACK=REACHED; else echo LOOPBACK=DENIED; fi");

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"echo \"PID=$$\"\n");
        builder.Append(CultureInfo.InvariantCulture, $"if [ -e {Quote(homeCanaryPath)} ]; then echo HOMECANARY=PRESENT; else echo HOMECANARY=ABSENT; fi\n");
        builder.Append(CultureInfo.InvariantCulture, $"if [ -e {Quote(siblingCanaryPath)} ]; then echo SIBLINGCANARY=PRESENT; else echo SIBLINGCANARY=ABSENT; fi\n");
        builder.Append(CultureInfo.InvariantCulture, $"if [ -e '/proc/{Environment.ProcessId}' ]; then echo HOSTPID=VISIBLE; else echo HOSTPID=INVISIBLE; fi\n");
        builder.Append("if echo ok > /work/xe-probe.txt 2>/dev/null; then echo WORKWRITE=OK; else echo WORKWRITE=DENIED; fi\n");
        builder.Append("if echo ok > /tmp/xe-probe.txt 2>/dev/null; then echo TMPWRITE=OK; else echo TMPWRITE=DENIED; fi\n");
        // /dev is expected to answer EROFS specifically: that is what a remounted-read-only mount says, and it
        // distinguishes "the remount worked" from "the path happened not to exist".
        builder.Append("DEVERR=$(touch /dev/xe-probe 2>&1); if [ -e /dev/xe-probe ]; then echo DEVCREATE=OK; else case \"$DEVERR\" in *\"Read-only\"*) echo DEVCREATE=EROFS;; *) echo DEVCREATE=REFUSED;; esac; fi\n");
        // procfs refuses creation with ENOENT rather than EROFS — it has no directory to create in — so the control is
        // "refused", not "read-only". Measured on this host; asserting EROFS here would fail on a working sandbox.
        builder.Append("if touch /proc/xe-probe 2>/dev/null; then echo PROCCREATE=OK; else echo PROCCREATE=REFUSED; fi\n");
        builder.Append("if echo x > /dev/null 2>/dev/null; then echo DEVNULL=OK; else echo DEVNULL=BROKEN; fi\n");
        builder.Append("if [ \"$(head -c 4 /dev/urandom | wc -c)\" = \"4\" ]; then echo URANDOM=OK; else echo URANDOM=BROKEN; fi\n");
        builder.Append("if head -n 1 /proc/self/status > /dev/null 2>&1; then echo PROCREAD=OK; else echo PROCREAD=BROKEN; fi\n");
        builder.Append("if touch /xe-probe 2>/dev/null; then echo ROOTWRITE=OK; else echo ROOTWRITE=DENIED; fi\n");
        builder.Append("if touch /usr/xe-probe 2>/dev/null; then echo USRWRITE=OK; else echo USRWRITE=DENIED; fi\n");
        builder.Append("if touch /etc/xe-probe 2>/dev/null; then echo ETCWRITE=OK; else echo ETCWRITE=DENIED; fi\n");
        builder.Append("echo \"PASSWD=$(grep -c . /etc/passwd)\"\n");
        builder.Append("if [ -d /run ]; then echo RUN=YES; else echo RUN=NO; fi\n");
        builder.Append("echo \"RUNENTRIES=$(ls -A /run | wc -l)\"\n");
        builder.Append(CultureInfo.InvariantCulture, $"if [ -e {busPath} ]; then echo BUS=PRESENT; else echo BUS=ABSENT; fi\n");
        builder.Append("if [ -e /run/docker.sock ]; then echo DOCKER=PRESENT; else echo DOCKER=ABSENT; fi\n");
        builder.Append(loopback).Append('\n');
        builder.Append("echo DONE=1\n");

        return builder.ToString();
    }

    /// <summary>
    ///     Renders <paramref name="value" /> as ONE POSIX shell word: wrapped in single quotes, with every embedded
    ///     apostrophe closed, escaped and reopened as <c>'\''</c>. That is the only quoting form a POSIX shell reads
    ///     literally with no exceptions — no expansion, no escape processing — so a path containing a quote, a space,
    ///     a dollar sign or a backtick reaches <c>[ -e … ]</c> as itself.
    /// </summary>
    private static string Quote(string value)
    {
        return string.Concat("'", value.Replace("'", @"'\''", StringComparison.Ordinal), "'");
    }

    private static (int ExitCode, string Output) RunChain(IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> userBusEnvironment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = chain[0],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in chain.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The chain starts from a scrubbed environment for the same reason a real command does; only the bus address
        // the outer systemd-run needs is added, and bwrap's --clearenv removes even that before the shell runs.
        startInfo.Environment.Clear();
        foreach (var pair in userBusEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = Process.Start(startInfo)
                            ?? throw new SandboxIsolationUnavailableException("the isolated chain could not be started");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProbeTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // Already gone; nothing further to do for a probe.
            }

            throw new SandboxIsolationUnavailableException("the isolated chain did not finish within the probe timeout");
        }

        var output = string.Concat(standardOutput.GetAwaiter().GetResult(), "\n", standardError.GetAwaiter().GetResult());

        return (process.ExitCode, output);
    }

    private static Dictionary<string, string> ParseFacts(string output)
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                facts[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return facts;
    }

    private static void AddWhenMissing(List<string> missing, string? resolved, string name)
    {
        if (resolved is null)
        {
            missing.Add(name);
        }
    }

    private static SandboxFilesystemIsolationProbeResult Unavailable(string reason)
    {
        return new SandboxFilesystemIsolationProbeResult(Isolation: null, reason);
    }

    /// <summary>
    ///     A throwaway 0700 jail for the probe plus the two canary files whose invisibility inside it is the point:
    ///     one under the engine user's home, one BESIDE the jail, where the sandboxes of other callers live.
    /// </summary>
    private sealed class ProbeScratch : IDisposable
    {
        private readonly string _root;

        public ProbeScratch()
        {
            var identifier = Guid.NewGuid().ToString("N");
            _root = Path.Combine(SandboxPaths.ContainerRoot, $"isolation-probe-{identifier}");
            Directory.CreateDirectory(_root);
            JailPath = Path.Combine(_root, "jail");
            Directory.CreateDirectory(JailPath);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(JailPath, PrivateDirectoryMode);
            }

            SiblingCanaryPath = Path.Combine(_root, $"canary-{identifier}");
            File.WriteAllText(SiblingCanaryPath, "xe-isolation-probe");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            HomeCanaryPath = string.IsNullOrEmpty(home)
                ? SiblingCanaryPath
                : Path.Combine(home, $".xe-isolation-probe-{identifier}");
            if (!string.Equals(HomeCanaryPath, SiblingCanaryPath, StringComparison.Ordinal))
            {
                File.WriteAllText(HomeCanaryPath, "xe-isolation-probe");
            }
        }

        public string JailPath { get; }

        public string HomeCanaryPath { get; }

        public string SiblingCanaryPath { get; }

        public bool CanariesStillExist()
        {
            return File.Exists(HomeCanaryPath) && File.Exists(SiblingCanaryPath);
        }

        public void Dispose()
        {
            TryDeleteFile(HomeCanaryPath);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort probe cleanup.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort probe cleanup.
            }
        }
    }

    /// <summary>
    ///     A loopback listener on an ephemeral port, used as the target of the egress control. It has to be REACHABLE
    ///     from the host for the control to mean anything, which is asserted before the sandbox is asked to fail.
    /// </summary>
    private sealed class HostLoopbackListener : IDisposable
    {
        private readonly TcpListener _listener;

        public HostLoopbackListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        public bool CanBeReachedFromTheHost()
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, Port);

                return client.Connected;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    private static extern uint geteuid();

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getegid")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
    private static extern uint getegid();
}
