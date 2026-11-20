namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>The real <see cref="IWindowsJobLaunchNative" />: kernel32, nothing else.</summary>
/// <remarks>
///     Two <c>CreateProcessW</c> shapes: the helper (the system <c>cmd.exe</c> by full path, suspended, no inherited handle, in the parent
///     job) and the command (suspended, in its job, parented by the helper, inheriting exactly three helper-local values). A parked value
///     is a raw value in the helper's handle table and is never wrapped in a <see cref="SafeHandle" />: disposing one would close an
///     unrelated engine handle.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobLaunchNative : IWindowsJobLaunchNative
{
    private const uint WaitObject0 = 0x0000_0000;
    private const uint WaitTimeout = 0x0000_0102;

    private static readonly string CommandInterpreter = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public SafeHandle CreateParentJob()
    {
        var job = WindowsJobNative.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            WindowsJobNative.SetInformation(job,
                WindowsJobNative.JobObjectExtendedLimitInformation,
                new WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new WindowsJobNative.JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = WindowsJobLimits.LimitKillOnJobClose }
                });
            var flags = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(job,
                WindowsJobNative.JobObjectExtendedLimitInformation).BasicLimitInformation.LimitFlags;
            if (flags != WindowsJobLimits.LimitKillOnJobClose)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"the parent job reads back limit flags 0x{flags:X}, not kill-on-close alone"));
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public WindowsJobCreatedProcess CreateHelper(SafeHandle parentJob) =>
        Create(CommandInterpreter,
            $"\"{CommandInterpreter}\" /d /c exit 0",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory) ?? Environment.SystemDirectory },
            Environment.SystemDirectory,
            parentJob,
            parent: null,
            parked: []);

    public IntPtr Park(SafeHandle helper, SafeHandle end)
    {
        // The one inheritable duplicate the launcher makes, and it lives in the helper's table, never the engine's.
        if (!WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(),
                end,
                helper,
                out var parked,
                0,
                bInheritHandle: true,
                WindowsJobNative.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return parked;
    }

    public WindowsJobCreatedProcess CreateCommand(SafeHandle job, SafeHandle helper, WindowsJobStartInfo startInfo, ReadOnlySpan<IntPtr> parked)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (parked.Length != 3)
        {
            throw new ArgumentException("a command inherits exactly its three parked stdio values", nameof(parked));
        }

        return Create(lpApplicationName: null,
            startInfo.CommandLine,
            startInfo.Environment,
            string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
            job,
            helper,
            parked);
    }

    public void Unpark(SafeHandle helper, IntPtr parked)
    {
        if (!WindowsJobNative.DuplicateHandle(helper,
                parked,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                bInheritHandle: false,
                WindowsJobNative.DuplicateCloseSource))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        if (!WindowsJobNative.IsProcessInJob(process, job, out var member))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return member;
    }

    public void TerminateProcess(SafeHandle process)
    {
        if (!WindowsJobNative.TerminateProcess(process, uExitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public WindowsJobExitWait Wait(SafeHandle process, TimeSpan timeout, out int error)
    {
        var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        var result = WindowsJobNative.WaitForSingleObject(process, milliseconds);
        error = result is WaitObject0 or WaitTimeout ? 0 : Marshal.GetLastPInvokeError();
        return result switch
        {
            WaitObject0 => WindowsJobExitWait.Signalled,
            WaitTimeout => WindowsJobExitWait.TimedOut,
            _ => WindowsJobExitWait.Failed
        };
    }

    [SuppressMessage("Sonar Code Smell",
        "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification = "PROC_THREAD_ATTRIBUTE_JOB_LIST and PROC_THREAD_ATTRIBUTE_PARENT_PROCESS take raw handle values; each SafeHandle is AddRef'd for the whole call and released only after the attribute list is deleted.")]
    private static WindowsJobCreatedProcess Create(string? lpApplicationName,
        string commandLine,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory,
        SafeHandle job,
        SafeHandle? parent,
        ReadOnlySpan<IntPtr> parked)
    {
        var line = (commandLine + '\0').ToCharArray();
        var block = BuildEnvironmentBlock(environment);
        // The command carries PARENT_PROCESS, JOB_LIST and HANDLE_LIST; the helper carries JOB_LIST alone.
        var count = parent is null ? 1 : 3;

        nint size = 0;
        _ = WindowsJobNative.InitializeProcThreadAttributeList(IntPtr.Zero, count, dwFlags: 0, ref size);
        // Allocated inside the try, so a failed allocation frees the earlier ones; FreeHGlobal of IntPtr.Zero does nothing.
        IntPtr attributes = IntPtr.Zero, jobs = IntPtr.Zero, parentSlot = IntPtr.Zero, inherited = IntPtr.Zero;
        var initialized = false;
        bool jobRef = false, parentRef = false;
        try
        {
            attributes = Marshal.AllocHGlobal(size);
            // Every value slot must outlive the attribute list, which stores their addresses.
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            job.DangerousAddRef(ref jobRef);
            if (!WindowsJobNative.InitializeProcThreadAttributeList(attributes, count, dwFlags: 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            initialized = true;
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            Update(attributes, WindowsJobNative.ProcThreadAttributeJobList, jobs, IntPtr.Size);

            var startup = new WindowsJobNative.STARTUPINFOEXW
            {
                StartupInfo = new WindowsJobNative.STARTUPINFOW { cb = (uint)Marshal.SizeOf<WindowsJobNative.STARTUPINFOEXW>() },
                lpAttributeList = attributes
            };
            if (parent is not null)
            {
                parentSlot = Marshal.AllocHGlobal(IntPtr.Size);
                inherited = Marshal.AllocHGlobal(3 * IntPtr.Size);
                parent.DangerousAddRef(ref parentRef);
                Marshal.WriteIntPtr(parentSlot, parent.DangerousGetHandle());
                Update(attributes, WindowsJobNative.ProcThreadAttributeParentProcess, parentSlot, IntPtr.Size);
                for (var index = 0; index < 3; index++)
                {
                    Marshal.WriteIntPtr(inherited, index * IntPtr.Size, parked[index]);
                }

                Update(attributes, WindowsJobNative.ProcThreadAttributeHandleList, inherited, 3 * IntPtr.Size);
                startup.StartupInfo.dwFlags = WindowsJobNative.StartfUseStdHandles;
                startup.StartupInfo.hStdInput = parked[0];
                startup.StartupInfo.hStdOutput = parked[1];
                startup.StartupInfo.hStdError = parked[2];
            }

            // Never a UI-limited job and never a breakaway flag: the job's own flags are the whole story.
            const uint flags = WindowsJobNative.CreateSuspended
                               | WindowsJobNative.CreateUnicodeEnvironment
                               | WindowsJobNative.ExtendedStartupInfoPresent
                               | WindowsJobNative.CreateNoWindow;
            if (!WindowsJobNative.CreateProcessW(lpApplicationName,
                    line,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: parent is not null,
                    flags,
                    block,
                    workingDirectory,
                    ref startup,
                    out var created))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new WindowsJobCreatedProcess
            {
                Process = new SafeProcessHandle(created.hProcess, ownsHandle: true),
                Thread = new SafeWaitHandle(created.hThread, ownsHandle: true),
                ProcessId = checked((int)created.dwProcessId)
            };
        }
        finally
        {
            if (initialized)
            {
                WindowsJobNative.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(parentSlot);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(attributes);
            if (parentRef)
            {
                parent!.DangerousRelease();
            }

            if (jobRef)
            {
                job.DangerousRelease();
            }
        }
    }

    private static void Update(IntPtr attributes, IntPtr attribute, IntPtr value, nint size)
    {
        if (!WindowsJobNative.UpdateProcThreadAttribute(attributes, 0, attribute, value, size, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // NAME=value\0 ... \0, sorted case-insensitively as Windows and .NET's Process sort the block.
    private static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var builder = new StringBuilder();
        foreach (var name in environment.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(name).Append('=').Append(environment[name]).Append('\0');
        }

        if (builder.Length == 0)
        {
            builder.Append('\0');
        }

        return builder.Append('\0').ToString().ToCharArray();
    }
}
