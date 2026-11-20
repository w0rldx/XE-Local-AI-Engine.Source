namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Globalization;

/// <summary>Proves the plain-path job ceilings on this host through the SAME <see cref="WindowsJobLauncher" /> every command uses (ADR 0025).</summary>
/// <remarks>
///     A commit under the job limit succeeds and one past it fails with the commitment-limit error; a chain inside the cap runs, and one
///     past it cannot start its last member while the same chain under a wider cap can. Every launch reads every limit back, so a refused
///     CPU rate or a launcher-breaking ancestor job fails a control. Only the system <c>cmd.exe</c> runs, and any failure is an unsupported
///     result with its reason. The result is cached for the process (ADR 0025).
/// </remarks>
public static class WindowsJobCeilingsProbe
{
    internal const int ProbeMemoryMb = 256;
    internal const int ProbeProcessLimit = 4;
    internal const ulong UnderLimitCommitBytes = 32UL * 1_048_576;
    internal const ulong PastLimitCommitBytes = 512UL * 1_048_576;
    internal const int ChainExitCode = 7;

    // A console child may bring its own conhost into the job, so the chains sit clear of the cap on both sides: two cmd processes (three
    // with conhost) stay inside four, five (six with conhost) cannot.
    internal const int ChainInsideCap = 2;
    internal const int ChainPastCap = 5;

    // The control for the past-cap chain: the same five-deep chain under a cap it fits in must run, so its failure under four is the cap's.
    internal const int WideProcessLimit = 8;

    // ERROR_COMMITMENT_LIMIT: the error a commit past JOB_OBJECT_LIMIT_JOB_MEMORY returns (ADR 0025, Context).
    internal const int CommitmentLimitError = 1455;

    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs the controls with the host's own <c>cmd.exe</c>.</summary>
    public static WindowsJobProbeResult Measure(WindowsJobLauncher launcher) =>
        Measure(launcher, Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    internal static WindowsJobProbeResult Measure(WindowsJobLauncher launcher, string commandInterpreter)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        try
        {
            var processors = launcher.SystemProcessorCount;
            if (processors < 1)
            {
                return Unsupported("the system logical processor count could not be read");
            }

            var limits = WindowsJobLimits.Map(ProbeMemoryMb, ProbeProcessLimit, cpuCount: 1, processors);
            var wide = WindowsJobLimits.Map(ProbeMemoryMb, WideProcessLimit, cpuCount: 1, processors);
            return MeasureCommit(launcher, commandInterpreter, limits)
                   ?? MeasureChain(launcher, commandInterpreter, limits, ProbeProcessLimit, ChainInsideCap, expectStart: true)
                   ?? MeasureChain(launcher, commandInterpreter, wide, WideProcessLimit, ChainPastCap, expectStart: true)
                   ?? MeasureChain(launcher, commandInterpreter, limits, ProbeProcessLimit, ChainPastCap, expectStart: false)
                   ?? new WindowsJobProbeResult
                   {
                       Supported = true,
                       SystemProcessorCount = processors
                   };
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException
                                              or ArgumentException)
        {
            return Unsupported(exception.Message);
        }
    }

    private static WindowsJobProbeResult? MeasureCommit(WindowsJobLauncher launcher, string commandInterpreter, WindowsJobLimits limits)
    {
        // An interactive cmd reading its open stdin stays alive and idle while the engine commits inside its job.
        using var holder = launcher.Start(StartInfo(commandInterpreter, ["/d", "/q", "/k"]) with { RedirectStandardInput = true }, limits);
        try
        {
            if (!holder.TryCommit(UnderLimitCommitBytes, out var underError))
            {
                return Unsupported(Invariant($"a {UnderLimitCommitBytes / 1_048_576} MB commit inside a {ProbeMemoryMb} MB job failed (error {underError})"));
            }

            if (holder.TryCommit(PastLimitCommitBytes, out var pastError))
            {
                return Unsupported(Invariant($"a {PastLimitCommitBytes / 1_048_576} MB commit inside a {ProbeMemoryMb} MB job succeeded, so the job memory limit is not enforced"));
            }

            return pastError == CommitmentLimitError
                ? null
                : Unsupported(Invariant($"a commit past the job memory limit failed with error {pastError}, not ERROR_COMMITMENT_LIMIT, so the failure is not the job's"));
        }
        finally
        {
            holder.Terminate();
        }
    }

    private static WindowsJobProbeResult? MeasureChain(WindowsJobLauncher launcher,
        string commandInterpreter,
        WindowsJobLimits limits,
        int cap,
        int depth,
        bool expectStart)
    {
        // cmd /d /c cmd /d /c ... exit 7: the code reaches the root only if every member started.
        var arguments = new List<string>();
        for (var level = 1; level < depth; level++)
        {
            arguments.AddRange(["/d", "/c", "cmd"]);
        }

        arguments.AddRange(["/d", "/c", "exit", ChainExitCode.ToString(CultureInfo.InvariantCulture)]);
        using var chain = launcher.Start(StartInfo(commandInterpreter, arguments), limits);
        try
        {
            // The containment probe is synchronous by contract (one cached measurement behind ISandboxContainmentProbe); the wait is
            // bounded and completes on a thread-pool wait callback, so it cannot deadlock.
#pragma warning disable MA0045, MA0032
            var finished = chain.WaitForExitAsync(CancellationToken.None).Wait(ControlTimeout);
#pragma warning restore MA0045, MA0032
            if (!finished)
            {
                return Unsupported(Invariant($"a chain of {depth} processes did not finish within {ControlTimeout.TotalSeconds} s"));
            }
        }
        finally
        {
            chain.Terminate();
        }

        var started = chain.ExitCode == ChainExitCode;
        if (started == expectStart)
        {
            return null;
        }

        return Unsupported(expectStart
            ? Invariant($"a chain of {depth} processes inside a {cap}-process job exited {chain.ExitCode}, not {ChainExitCode}")
            : Invariant($"a chain of {depth} processes ran inside a {cap}-process job, so the active-process limit is not enforced"));
    }

    private static WindowsJobStartInfo StartInfo(string commandInterpreter, IEnumerable<string> arguments)
    {
        var system = Path.GetDirectoryName(commandInterpreter) ?? Environment.SystemDirectory;
        var quoted = commandInterpreter.Contains(' ', StringComparison.Ordinal) ? $"\"{commandInterpreter}\"" : commandInterpreter;
        return new WindowsJobStartInfo
        {
            CommandLine = string.Join(' ', [quoted, .. arguments]),
            WorkingDirectory = system,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemRoot"] = Path.GetDirectoryName(system) ?? system,
                ["PATH"] = system,
                ["ComSpec"] = commandInterpreter
            }
        };
    }

    private static WindowsJobProbeResult Unsupported(string reason) =>
        new()
        {
            Supported = false,
            Reason = $"the Windows Job Object ceilings probe failed: {reason}"
        };

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);
}
