namespace XE_Local_AI_Engine.Desktop;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>Names the process for the Windows shell, so the taskbar groups the window as XE instead of ".NET Host".</summary>
internal static partial class DesktopWindowsShell
{
    internal const string AppUserModelId = "XE.LocalAIEngine.Desktop";

    /// <summary>Sets the explicit AppUserModelID; must run before any window or tray icon exists. A failure is ignored.</summary>
    [SupportedOSPlatform("windows")]
    internal static void NameProcess() => _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
