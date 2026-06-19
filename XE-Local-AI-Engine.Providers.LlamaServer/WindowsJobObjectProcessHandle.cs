namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     Windows process handle that contains the child (and any process it spawns) in a Job Object configured with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Closing the job handle — on <see cref="TreeKill" /> or
///     <see cref="Dispose" /> — terminates the entire tree, so no orphan survives a supervisor stop or crash. All
///     native calls are reached only on Windows (the launcher guards with <see cref="OperatingSystem.IsWindows" />).
/// </summary>
/// <remarks>
///     <para>
///         <strong>Operator-verification flag:</strong> this WSL2/Linux build cannot exercise the Win32 path. The
///         signatures, struct layout, and SafeHandle ownership follow the global <c>dotnet-pinvoke</c> standard and the
///         logic is unit-tested through the launcher seam, but real tree-kill behavior MUST be verified on Windows 11.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsJobObjectProcessHandle : ILlamaServerProcessHandle
{
    // ── Win32 interop ────────────────────────────────────────────────────────────────────────────────────────────

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private readonly SafeJobHandle _job;
    private readonly Process _process;
    private int _disposed;

    private WindowsJobObjectProcessHandle(Process process, SafeJobHandle job)
    {
        _process = process;
        _job = job;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => SafeHasExited(_process);

    public void TreeKill()
    {
        // Closing the kill-on-close job terminates the whole tree. Idempotent: SafeHandle guards double-close.
        if (!_job.IsClosed && !_job.IsInvalid)
        {
            _job.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            TreeKill();
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    ///     Creates a job, marks it kill-on-close, assigns the already-started <paramref name="process" /> to it, and
    ///     returns the handle. On any failure the job and process are torn down and a sanitized error is surfaced.
    /// </summary>
    public static WindowsJobObjectProcessHandle Wrap(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        SafeJobHandle? job = null;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            ConfigureKillOnClose(job);
            AssignProcess(job, process);
            return new WindowsJobObjectProcessHandle(process, job);
        }
        catch (Exception ex)
        {
            job?.Dispose();
            TryKill(process);
            process.Dispose();
            throw new LlamaRuntimeException("The local model runtime could not be contained for safe shutdown.", ex);
        }
    }

    private static void ConfigureKillOnClose(SafeJobHandle job)
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void AssignProcess(SafeJobHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!SafeHasExited(process))
            {
                process.Kill(true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup on the failure path; the sanitized LlamaRuntimeException already carries the cause.
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    /// <summary>Owns the Win32 job-object handle; closing it terminates the kill-on-close job's process tree.</summary>
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
