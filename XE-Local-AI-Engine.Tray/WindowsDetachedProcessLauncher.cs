namespace XE_Local_AI_Engine.Tray;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

internal static class WindowsDetachedProcessLauncher
{
    [SupportedOSPlatform("windows")]
    public static void StartDetached(string executablePath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var startupInfo = new NativeMethods.StartupInfoEx
        {
            StartupInfo = new NativeMethods.StartupInfo
            {
                Cb = (uint)Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                StandardInput = NativeMethods.InvalidHandleValue,
                StandardOutput = NativeMethods.InvalidHandleValue,
                StandardError = NativeMethods.InvalidHandleValue
            }
        };

        var creationFlags = NativeMethods.ProcessCreationOptions.DetachedProcess
                            | NativeMethods.ProcessCreationOptions.CreateNewProcessGroup
                            | NativeMethods.ProcessCreationOptions.CreateBreakawayFromJob
                            | NativeMethods.ProcessCreationOptions.ExtendedStartupInfoPresent;

        if (!NativeMethods.CreateProcessW(applicationName: executablePath,
                commandLine: null,
                processAttributes: IntPtr.Zero,
                threadAttributes: IntPtr.Zero,
                inheritHandles: false,
                creationFlags: creationFlags,
                environment: IntPtr.Zero,
                currentDirectory: workingDirectory,
                startupInfo: startupInfo,
                processInformation: out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeMethods.CloseHandle(processInformation.Thread);
        NativeMethods.CloseHandle(processInformation.Process);
    }

    private static class NativeMethods
    {
        /// <summary>
        ///     Configuration options for process creation behavior.
        /// </summary>
        [Flags]
        public enum ProcessCreationOptions : uint
        {
            DetachedProcess = 0x00000008,
            CreateNewProcessGroup = 0x00000200,
            ExtendedStartupInfoPresent = 0x00080000,
            CreateBreakawayFromJob = 0x01000000
        }

        public static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessW(string applicationName,
            string? commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)]
            bool inheritHandles,
            ProcessCreationOptions creationFlags,
            IntPtr environment,
            string currentDirectory,
            in StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        ///     Value object carrying startup info data.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public uint Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2Count;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        /// <summary>
        ///     Value object carrying startup info ex data.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        /// <summary>
        ///     Value object carrying process information data.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }
    }
}
