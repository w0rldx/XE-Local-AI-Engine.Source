namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>The real <see cref="IWindowsJobApi" />: kernel32, nothing else.</summary>
/// <remarks>
///     Handle rules (ADR 0025): every pipe end is created non-inheritable and stays so in the engine for its whole life. The three child
///     ends reach the command only as inheritable duplicates parked in <see cref="WindowsJobHandleParent" />'s helper, so a concurrent
///     <c>Process.Start</c> inherits none of them. The engine's copies are closed right after the create; stdout and stderr are read
///     overlapped.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobApi : IWindowsJobApi
{
    private const uint PipeBufferBytes = 65_536;

    private readonly WindowsJobHandleParent _handleParent;

    public WindowsJobApi()
        : this(WindowsJobHandleParent.Shared)
    {
    }

    internal WindowsJobApi(WindowsJobHandleParent handleParent)
    {
        ArgumentNullException.ThrowIfNull(handleParent);
        _handleParent = handleParent;
    }

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
        Justification = "The parent ends are owned by the returned child; every failure path disposes all of them, and the handle parent owns the process until the child exists.")]
    public WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null;
        try
        {
            CreatePipe(out stdinRead, out stdinWrite);
            CreateOverlappedReadPipe(out stdoutRead, out stdoutWrite);
            CreateOverlappedReadPipe(out stderrRead, out stderrWrite);
            try
            {
                return _handleParent.CreateCommand(job, startInfo, stdinRead, stdoutWrite, stderrWrite, created => Adopt(created, stdinWrite, stdoutRead, stderrRead));
            }
            finally
            {
                // The command holds its own copies now; the engine's must not outlive the launch.
                stdinRead.Dispose();
                stdoutWrite.Dispose();
                stderrWrite.Dispose();
            }
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

    public WindowsJobScheduling GetEngineScheduling()
    {
        if (!WindowsJobNative.GetProcessAffinityMask(WindowsJobNative.GetCurrentProcess(), out var affinityMask, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Zero: the engine's threads span several processor groups and no one mask describes them; refusing beats narrowing a command.
        if (affinityMask == 0)
        {
            throw new InvalidOperationException("the engine's threads span several processor groups, so it has no affinity mask a command can follow");
        }

        var priorityClass = WindowsJobNative.GetPriorityClass(WindowsJobNative.GetCurrentProcess());
        if (priorityClass == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new WindowsJobScheduling
        {
            AffinityMask = affinityMask,
            PriorityClass = priorityClass
        };
    }

    public void SetScheduling(SafeHandle process, WindowsJobScheduling scheduling)
    {
        if (!WindowsJobNative.SetProcessAffinityMask(process, scheduling.AffinityMask)
            || !WindowsJobNative.SetPriorityClass(process, scheduling.PriorityClass))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
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

    public string? TerminateAndConfirm(SafeHandle process, int processId) =>
        _handleParent.TerminateAndConfirm(process, processId);

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

    /// <summary>An overlapped server end the engine reads and a synchronous client end the child writes.</summary>
    /// <remarks>
    ///     Windows documents cancelling a blocked read on another thread only for overlapped I/O, so the drain budget needs this read end
    ///     overlapped. One instance, local clients only, a GUID name: the engine's own client open is the pipe's only connection.
    /// </remarks>
    private static void CreateOverlappedReadPipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var name = string.Create(CultureInfo.InvariantCulture, $@"\\.\pipe\xe-sandbox-{Environment.ProcessId}-{Guid.NewGuid():N}");
        read = WindowsJobNative.CreateNamedPipeW(name,
            WindowsJobNative.PipeAccessInbound | WindowsJobNative.FileFlagOverlapped | WindowsJobNative.FileFlagFirstPipeInstance,
            WindowsJobNative.PipeRejectRemoteClients,
            nMaxInstances: 1,
            nOutBufferSize: PipeBufferBytes,
            nInBufferSize: PipeBufferBytes,
            nDefaultTimeOut: 0,
            IntPtr.Zero);
        if (read.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write = new SafeFileHandle();
            throw new Win32Exception(error);
        }

        write = WindowsJobNative.CreateFileW(name, WindowsJobNative.GenericWrite, 0, IntPtr.Zero, WindowsJobNative.OpenExisting, 0, IntPtr.Zero);
        if (write.IsInvalid || (!WindowsJobNative.ConnectNamedPipe(read, IntPtr.Zero) && Marshal.GetLastPInvokeError() != WindowsJobNative.ErrorPipeConnected))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error);
        }
    }

    // Every partial stream is disposed on a failure here; the handle parent then ends the command it still owns.
    private static WindowsJobChild Adopt(WindowsJobCreatedProcess created, SafeFileHandle stdinWrite, SafeFileHandle stdoutRead, SafeFileHandle stderrRead)
    {
        FileStream? input = null, output = null, error = null;
        try
        {
            input = new FileStream(stdinWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
            output = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 4096, isAsync: true);
            error = new FileStream(stderrRead, FileAccess.Read, bufferSize: 4096, isAsync: true);
            return new WindowsJobChild
            {
                Process = created.Process,
                Thread = created.Thread,
                ProcessId = created.ProcessId,
                StandardInput = input,
                StandardOutput = output,
                StandardError = error
            };
        }
        catch
        {
            // Nothing was written through these streams, so a synchronous dispose only closes their handles.
#pragma warning disable MA0045
            input?.Dispose();
            output?.Dispose();
            error?.Dispose();
#pragma warning restore MA0045
            throw;
        }
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }
}
