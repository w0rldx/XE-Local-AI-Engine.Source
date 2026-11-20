namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>The Job Object and process-creation interop, in one place for every job this engine creates.</summary>
/// <remarks>
///     There is deliberately no <c>JOBOBJECT_BASIC_UI_RESTRICTIONS</c> class or setter here: an engine-side job must never carry UI limits
///     (ADR 0025), and a test scans this project for the name.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class WindowsJobNative
{
    public const int JobObjectExtendedLimitInformation = 9;
    public const int JobObjectCpuRateControlInformation = 15;

    public const uint CpuRateControlEnable = 0x1;
    public const uint CpuRateControlHardCap = 0x4;

    public const uint CreateSuspended = 0x0000_0004;
    public const uint CreateUnicodeEnvironment = 0x0000_0400;
    public const uint ExtendedStartupInfoPresent = 0x0008_0000;
    public const uint CreateNoWindow = 0x0800_0000;

    public const uint StartfUseStdHandles = 0x0000_0100;

    public const uint PipeAccessInbound = 0x0000_0001;
    public const uint FileFlagFirstPipeInstance = 0x0008_0000;
    public const uint FileFlagOverlapped = 0x4000_0000;
    public const uint PipeRejectRemoteClients = 0x0000_0008;
    public const uint GenericWrite = 0x4000_0000;
    public const uint OpenExisting = 3;
    public const int ErrorPipeConnected = 535;
    public const uint Synchronize = 0x0010_0000;
    public const uint DuplicateCloseSource = 0x1;
    public const uint DuplicateSameAccess = 0x2;

    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint MemRelease = 0x8000;
    public const uint PageReadWrite = 0x04;

    public const ushort AllProcessorGroups = 0xFFFF;

    // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, _HANDLE_LIST and _JOB_LIST (ProcThreadAttributeJobList = 13 | PROC_THREAD_ATTRIBUTE_INPUT).
    public static readonly IntPtr ProcThreadAttributeParentProcess = 0x0002_0000;
    public static readonly IntPtr ProcThreadAttributeHandleList = 0x0002_0002;
    public static readonly IntPtr ProcThreadAttributeJobList = 0x0002_000D;

    /// <summary>Sets one fixed-size information class on a job, throwing the Win32 error on failure.</summary>
    public static void SetInformation<T>(SafeHandle job, int informationClass, T value)
        where T : struct
    {
        var length = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(value, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, informationClass, buffer, (uint)length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads one fixed-size information class back from a job, throwing the Win32 error on failure.</summary>
    public static T QueryInformation<T>(SafeHandle job, int informationClass)
        where T : struct
    {
        var length = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!QueryInformationJobObject(job, informationClass, buffer, (uint)length, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return Marshal.PtrToStructure<T>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeJobHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeHandle hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        out uint lpReturnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeHandle hJob, IntPtr hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeHandle hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeHandle processHandle, SafeHandle jobHandle, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle CreateNamedPipeW(string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        IntPtr lpSecurityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial SafeFileHandle CreateFileW(string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConnectNamedPipe(SafeHandle hNamedPipe, IntPtr lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeHandle hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        nint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(string? lpApplicationName,
        char[] lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        char[] lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEXW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint ResumeThread(SafeHandle hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeHandle hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(IntPtr hSourceProcessHandle,
        SafeHandle hSourceHandle,
        IntPtr hTargetProcessHandle,
        out SafeWaitHandle lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    /// <summary>Duplicates an engine handle into another process: the result is a value in THAT process's table, so it is never wrapped.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(IntPtr hSourceProcessHandle,
        SafeHandle hSourceHandle,
        SafeHandle hTargetProcessHandle,
        out IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    /// <summary>Acts on a value in another process's table; with <see cref="DuplicateCloseSource" /> and no target it closes that value.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateHandle(SafeHandle hSourceProcessHandle,
        IntPtr hSourceHandle,
        IntPtr hTargetProcessHandle,
        IntPtr lpTargetHandle,
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwOptions);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint WaitForSingleObject(SafeHandle hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial IntPtr VirtualAllocEx(SafeHandle hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFreeEx(SafeHandle hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    public struct IO_COUNTERS
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
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    // ControlFlags, then the CpuRate member of the native union (Weight and MinRate/MaxRate share its four bytes).
    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    public struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    public struct STARTUPINFOW
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    public struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SuppressMessage("Major Code Smell", "S101:Types should be named in PascalCase", Justification = "Win32 interop struct — name mirrors the native layout exactly.")]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
