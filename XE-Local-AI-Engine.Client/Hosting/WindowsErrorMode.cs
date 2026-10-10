namespace XE_Local_AI_Engine.Client.Hosting;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>
///     Turns off the Windows hard-error and fault dialogs for this process, so a child that fails to initialise exits instead of
///     waiting on a dialog nobody sees.
/// </summary>
/// <remarks>
///     Child processes inherit the error mode: a sandboxed MCP server dying with 0xC0000142 (Win32k denied) then exits at once and its
///     stdout closes, where the dialog kept it alive past the connect timeout. Whether MXC's ProcessContainer spawn passes the mode on
///     is checked by a Windows run only; no test on this platform can observe it.
/// </remarks>
internal static class WindowsErrorMode
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    /// <summary>Adds SEM_FAILCRITICALERRORS and SEM_NOGPFAULTERRORBOX to the error mode, keeping any flag already set.</summary>
    [SupportedOSPlatform("windows")]
    public static void SuppressHardErrorDialogs()
    {
        _ = SetErrorMode(GetErrorMode() | SemFailCriticalErrors | SemNoGpFaultErrorBox);
    }

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetErrorMode(uint mode);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetErrorMode();
}
