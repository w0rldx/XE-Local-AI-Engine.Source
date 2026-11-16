namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>
///     The exact Job Object values one command's job is configured with, and the values a read-back must return (ADR 0025).
/// </summary>
/// <remarks>
///     Platform-neutral on purpose: <see cref="Map" /> is a pure function, so the 64-bit memory math, the CPU denominator and the flag set are
///     asserted on any host. There is deliberately no UI-restriction member: a job that holds an MXC-spawning process or the desktop shell
///     must never carry UI limits, which break both (ADR 0025, Context).
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct WindowsJobLimits
{
    /// <summary><c>JOB_OBJECT_LIMIT_ACTIVE_PROCESS</c>.</summary>
    public const uint LimitActiveProcess = 0x0000_0008;

    /// <summary><c>JOB_OBJECT_LIMIT_JOB_MEMORY</c>: a commit beyond the job-wide limit fails; nothing is killed.</summary>
    public const uint LimitJobMemory = 0x0000_0200;

    /// <summary><c>JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION</c>: no error dialog can hold a member alive.</summary>
    public const uint LimitDieOnUnhandledException = 0x0000_0400;

    /// <summary><c>JOB_OBJECT_LIMIT_BREAKAWAY_OK</c>; never set, and a read-back carrying it fails the launch.</summary>
    public const uint LimitBreakawayOk = 0x0000_0800;

    /// <summary><c>JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK</c>; never set, and a read-back carrying it fails the launch.</summary>
    public const uint LimitSilentBreakawayOk = 0x0000_1000;

    /// <summary><c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the engine's handle, engine death included, kills every member.</summary>
    public const uint LimitKillOnJobClose = 0x0000_2000;

    /// <summary>The whole-system CPU rate unit: <c>CpuRate</c> is cycles per 10,000.</summary>
    public const uint FullCpuRate = 10_000;

    /// <summary>The <c>JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags</c> value; a read-back must match it exactly.</summary>
    public uint LimitFlags { get; init; }

    /// <summary>The job-wide commit limit in bytes, or 0 when no memory ceiling is set.</summary>
    public ulong JobMemoryLimit { get; init; }

    /// <summary>The most members that may be active at once, or 0 when no process ceiling is set.</summary>
    public uint ActiveProcessLimit { get; init; }

    /// <summary>The hard-capped CPU rate in cycles per 10,000 of the whole system, or 0 when no CPU ceiling is set.</summary>
    public uint CpuRate { get; init; }

    /// <summary>Maps one command's ceilings onto job values; a null or non-positive member leaves its limit out.</summary>
    /// <param name="memoryMb">The job-wide commit ceiling in MiB.</param>
    /// <param name="pidsLimit">The active-process ceiling; Windows has no thread ceiling, so this counts processes only.</param>
    /// <param name="cpuCount">The CPU ceiling in cores' worth of the system.</param>
    /// <param name="systemProcessorCount"><c>GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)</c>, never the process-relative count.</param>
    /// <remarks>An unnested job's rate is relative to the whole system, so a process-relative count would loosen the cap under affinity.</remarks>
    public static WindowsJobLimits Map(int? memoryMb, int? pidsLimit, double? cpuCount, int systemProcessorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(systemProcessorCount, 1);

        var flags = LimitKillOnJobClose | LimitDieOnUnhandledException;
        ulong memory = 0;
        if (memoryMb is > 0 and var mb)
        {
            flags |= LimitJobMemory;
            // 64-bit before the multiply: 2048 MiB in bytes already overflows a 32-bit int.
            memory = checked((ulong)mb * 1_048_576UL);
        }

        uint processes = 0;
        if (pidsLimit is > 0 and var pids)
        {
            flags |= LimitActiveProcess;
            processes = (uint)pids;
        }

        uint rate = 0;
        if (cpuCount is > 0 and var cores)
        {
            rate = (uint)Math.Clamp(Math.Ceiling(cores * FullCpuRate / systemProcessorCount), 1d, FullCpuRate);
        }

        return new WindowsJobLimits
        {
            LimitFlags = flags,
            JobMemoryLimit = memory,
            ActiveProcessLimit = processes,
            CpuRate = rate
        };
    }
}
