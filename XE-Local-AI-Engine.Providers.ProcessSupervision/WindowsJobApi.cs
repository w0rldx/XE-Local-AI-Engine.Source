namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>The real <see cref="IWindowsJobApi" />: kernel32, nothing else.</summary>
/// <remarks>
///     Handle rules (ADR 0025): every pipe is created non-inheritable. The three child ends become inheritable only inside
///     <see cref="LaunchLock" />, around the one <c>CreateProcessW</c> whose <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> names exactly them, and
///     the engine's copies are closed right after. The parent ends are never inheritable. .NET's own <c>Process.Start</c> inherits every
///     inheritable handle under a lock no other code can take, so a concurrent start inside that window can still inherit a child end;
///     the cost is a delayed end of output, which the drain budget of the reader bounds.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobApi : IWindowsJobApi
{
    private static readonly Lock LaunchLock = new();

    public int GetSystemProcessorCount() =>
        (int)WindowsJobNative.GetActiveProcessorCount(WindowsJobNative.AllProcessorGroups);

    public SafeHandle CreateJob()
    {
        var job = WindowsJobNative.CreateJobObjectW(IntPtr.Zero, lpName: null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    public void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits)
    {
        WindowsJobNative.SetInformation(job,
            WindowsJobNative.JobObjectExtendedLimitInformation,
            new WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new WindowsJobNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = limits.LimitFlags,
                    ActiveProcessLimit = limits.ActiveProcessLimit
                },
                JobMemoryLimit = checked((nuint)limits.JobMemoryLimit)
            });
    }

    public void SetCpuRate(SafeHandle job, uint cpuRate)
    {
        WindowsJobNative.SetInformation(job,
            WindowsJobNative.JobObjectCpuRateControlInformation,
            new WindowsJobNative.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
            {
                ControlFlags = WindowsJobNative.CpuRateControlEnable | WindowsJobNative.CpuRateControlHardCap,
                CpuRate = cpuRate
            });
    }

    public WindowsJobLimits QueryLimits(SafeHandle job)
    {
        var extended = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(job,
            WindowsJobNative.JobObjectExtendedLimitInformation);
        var cpu = WindowsJobNative.QueryInformation<WindowsJobNative.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION>(job,
            WindowsJobNative.JobObjectCpuRateControlInformation);
        var rate = cpu.ControlFlags switch
        {
            0 => 0u,
            WindowsJobNative.CpuRateControlEnable | WindowsJobNative.CpuRateControlHardCap => cpu.CpuRate,
            _ => throw new InvalidOperationException($"the Windows job reads back CPU rate control flags 0x{cpu.ControlFlags:X}, not a plain hard cap")
        };

        return new WindowsJobLimits
        {
            LimitFlags = extended.BasicLimitInformation.LimitFlags,
            JobMemoryLimit = extended.JobMemoryLimit,
            ActiveProcessLimit = extended.BasicLimitInformation.ActiveProcessLimit,
            CpuRate = rate
        };
    }

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The three parent ends are owned by the returned FileStreams; every failure path disposes all six ends.")]
    public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        try
        {
            CreatePipe(out stdinRead, out stdinWrite);
            CreatePipe(out stdoutRead, out stdoutWrite);
            CreatePipe(out stderrRead, out stderrWrite);

            WindowsJobNative.PROCESS_INFORMATION created;
            lock (LaunchLock)
            {
                try
                {
                    MakeInheritable(stdinRead);
                    MakeInheritable(stdoutWrite);
                    MakeInheritable(stderrWrite);
                    created = CreateProcess(job, startInfo, stdinRead, stdoutWrite, stderrWrite);
                }
                finally
                {
                    // The child holds its own copies now; the engine's must not outlive this window.
                    stdinRead.Dispose();
                    stdoutWrite.Dispose();
                    stderrWrite.Dispose();
                }
            }

            return new WindowsJobChild
            {
                Process = new SafeProcessHandle(created.hProcess, ownsHandle: true),
                Thread = new SafeWaitHandle(created.hThread, ownsHandle: true),
                ProcessId = checked((int)created.dwProcessId),
                StandardInput = new FileStream(stdinWrite, FileAccess.Write, bufferSize: 4096, isAsync: false),
                StandardOutput = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 4096, isAsync: false),
                StandardError = new FileStream(stderrRead, FileAccess.Read, bufferSize: 4096, isAsync: false)
            };
        }
        catch
        {
            stdinRead?.Dispose();
            stdinWrite?.Dispose();
            stdoutRead?.Dispose();
            stdoutWrite?.Dispose();
            stderrRead?.Dispose();
            stderrWrite?.Dispose();
            throw;
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

    public void Resume(SafeHandle thread)
    {
        if (WindowsJobNative.ResumeThread(thread) == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Terminate(SafeHandle job)
    {
        if (!WindowsJobNative.TerminateJobObject(job, uExitCode: 1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The wait handle is disposed by the continuation that unregisters it, once the process has exited.")]
    public Task WaitForExitAsync(SafeHandle process)
    {
        // A duplicate the registration owns: the caller may close the process handle while the wait is still registered.
        if (!WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(),
                process,
                WindowsJobNative.GetCurrentProcess(),
                out var duplicate,
                WindowsJobNative.Synchronize,
                bInheritHandle: false,
                dwOptions: 0))
        {
            var error = Marshal.GetLastPInvokeError();
            duplicate.Dispose();
            throw new Win32Exception(error);
        }

        var waitHandle = new ProcessExitWaitHandle(duplicate);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(waitHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            exited,
            Timeout.Infinite,
            executeOnlyOnce: true);
        return exited.Task.ContinueWith(_ =>
            {
                registration.Unregister(waitObject: null);
                waitHandle.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public int GetExitCode(SafeHandle process)
    {
        if (!WindowsJobNative.GetExitCodeProcess(process, out var exitCode))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return unchecked((int)exitCode);
    }

    public void CancelPendingReads(Stream stream)
    {
        // ERROR_NOT_FOUND (nothing pending) is the common answer and not a failure.
        if (stream is FileStream { SafeFileHandle: { IsClosed: false } handle })
        {
            _ = WindowsJobNative.CancelIoEx(handle, IntPtr.Zero);
        }
    }

    public bool TryCommit(SafeHandle process, ulong bytes, out int error)
    {
        var address = WindowsJobNative.VirtualAllocEx(process,
            IntPtr.Zero,
            checked((nuint)bytes),
            WindowsJobNative.MemCommit | WindowsJobNative.MemReserve,
            WindowsJobNative.PageReadWrite);
        if (address == IntPtr.Zero)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        error = 0;
        _ = WindowsJobNative.VirtualFreeEx(process, address, 0, WindowsJobNative.MemRelease);
        return true;
    }

    private static void CreatePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!WindowsJobNative.CreatePipe(out read, out write, IntPtr.Zero, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
    }

    private static void MakeInheritable(SafeHandle handle)
    {
        if (!WindowsJobNative.SetHandleInformation(handle, WindowsJobNative.HandleFlagInherit, WindowsJobNative.HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [SuppressMessage("Sonar Code Smell",
        "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification = "PROC_THREAD_ATTRIBUTE_JOB_LIST, PROC_THREAD_ATTRIBUTE_HANDLE_LIST and STARTUPINFO take raw handle values; each SafeHandle is AddRef'd for the whole call and released only after the attribute list is deleted.")]
    private static WindowsJobNative.PROCESS_INFORMATION CreateProcess(SafeHandle job,
        WindowsJobStartInfo startInfo,
        SafeHandle stdin,
        SafeHandle stdout,
        SafeHandle stderr)
    {
        var commandLine = (startInfo.CommandLine + '\0').ToCharArray();
        var environment = BuildEnvironmentBlock(startInfo.Environment);
        var workingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;

        nint size = 0;
        _ = WindowsJobNative.InitializeProcThreadAttributeList(IntPtr.Zero, dwAttributeCount: 2, dwFlags: 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        // Both arrays must outlive the attribute list, which stores their addresses.
        var jobs = Marshal.AllocHGlobal(IntPtr.Size);
        var inherited = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var initialized = false;
        bool jobRef = false, stdinRef = false, stdoutRef = false, stderrRef = false;
        try
        {
            job.DangerousAddRef(ref jobRef);
            stdin.DangerousAddRef(ref stdinRef);
            stdout.DangerousAddRef(ref stdoutRef);
            stderr.DangerousAddRef(ref stderrRef);
            if (!WindowsJobNative.InitializeProcThreadAttributeList(attributes, dwAttributeCount: 2, dwFlags: 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            initialized = true;
            Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 0, stdin.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, IntPtr.Size, stdout.DangerousGetHandle());
            Marshal.WriteIntPtr(inherited, 2 * IntPtr.Size, stderr.DangerousGetHandle());
            if (!WindowsJobNative.UpdateProcThreadAttribute(attributes, 0, WindowsJobNative.ProcThreadAttributeJobList, jobs, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)
                || !WindowsJobNative.UpdateProcThreadAttribute(attributes,
                    0,
                    WindowsJobNative.ProcThreadAttributeHandleList,
                    inherited,
                    3 * IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new WindowsJobNative.STARTUPINFOEXW
            {
                StartupInfo = new WindowsJobNative.STARTUPINFOW
                {
                    cb = (uint)Marshal.SizeOf<WindowsJobNative.STARTUPINFOEXW>(),
                    dwFlags = WindowsJobNative.StartfUseStdHandles,
                    hStdInput = stdin.DangerousGetHandle(),
                    hStdOutput = stdout.DangerousGetHandle(),
                    hStdError = stderr.DangerousGetHandle()
                },
                lpAttributeList = attributes
            };

            // Never a UI-limited job and never a breakaway flag: the job's own flags are the whole story.
            const uint flags = WindowsJobNative.CreateSuspended
                               | WindowsJobNative.CreateUnicodeEnvironment
                               | WindowsJobNative.ExtendedStartupInfoPresent
                               | WindowsJobNative.CreateNoWindow;
            if (!WindowsJobNative.CreateProcessW(lpApplicationName: null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    flags,
                    environment,
                    workingDirectory,
                    ref startup,
                    out var created))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return created;
        }
        finally
        {
            if (initialized)
            {
                WindowsJobNative.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(inherited);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(attributes);
            ReleaseIf(stderr, stderrRef);
            ReleaseIf(stdout, stdoutRef);
            ReleaseIf(stdin, stdinRef);
            ReleaseIf(job, jobRef);
        }
    }

    private static void ReleaseIf(SafeHandle handle, bool added)
    {
        if (added)
        {
            handle.DangerousRelease();
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

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}
