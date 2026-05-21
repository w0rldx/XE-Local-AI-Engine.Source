namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Runtime.InteropServices;

public static class WindowsConsoleControlHandler
{
    public static IDisposable Register(IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);

        if (!OperatingSystem.IsWindows() || NativeMethods.GetConsoleWindow() == IntPtr.Zero)
        {
            return NoopRegistration.Instance;
        }

        NativeMethods.ConsoleCtrlHandler handler = _ =>
        {
            applicationLifetime.StopApplication();
            return true;
        };

        return NativeMethods.SetConsoleCtrlHandler(handler, true)
            ? new ConsoleControlRegistration(handler)
            : NoopRegistration.Instance;
    }

    private sealed class ConsoleControlRegistration : IDisposable
    {
        private readonly NativeMethods.ConsoleCtrlHandler _handler;

        public ConsoleControlRegistration(NativeMethods.ConsoleCtrlHandler handler)
        {
            _handler = handler;
        }

        public void Dispose()
        {
            if (OperatingSystem.IsWindows())
            {
                NativeMethods.SetConsoleCtrlHandler(_handler, false);
            }
        }
    }

    private sealed class NoopRegistration : IDisposable
    {
        public static readonly NoopRegistration Instance = new();

        private NoopRegistration()
        {
        }

        public void Dispose()
        {
        }
    }

    private static class NativeMethods
    {
        public delegate bool ConsoleCtrlHandler(uint controlType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

        [DllImport("kernel32.dll", SetLastError = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr GetConsoleWindow();
    }
}
