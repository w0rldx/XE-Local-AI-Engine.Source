namespace XE_Local_AI_Engine.Desktop;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>Names the process for the Windows shell, so the taskbar groups the window as XE instead of ".NET Host".</summary>
internal static partial class DesktopWindowsShell
{
    internal const string AppUserModelId = "XE.LocalAIEngine.Desktop";

    /// <summary>The window message RegisterHotKey posts to the registering window; wParam carries the hotkey id.</summary>
    internal const uint HotkeyMessage = 0x0312;

    private const uint NoRepeat = 0x4000;
    private const uint EscapeKey = 0x1B;

    /// <summary>Sets the explicit AppUserModelID; must run before any window or tray icon exists. A failure is ignored.</summary>
    [SupportedOSPlatform("windows")]
    internal static void NameProcess() => _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    /// <summary>Registers a system-wide chord posting <see cref="HotkeyMessage" /> to the window; the Win32 error on failure.</summary>
    [SupportedOSPlatform("windows")]
    internal static int RegisterHotkey(nint window, int id, DesktopHotkey hotkey) =>
        RegisterHotKey(window, id, hotkey.Modifiers | NoRepeat, hotkey.VirtualKey) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>Claims a bare Escape while the overlay shows: the native WebView keeps key events from the shell.</summary>
    [SupportedOSPlatform("windows")]
    internal static bool RegisterEscape(nint window, int id) => RegisterHotKey(window, id, NoRepeat, EscapeKey);

    /// <summary>Releases a chord; a window that never got a handle registered nothing.</summary>
    [SupportedOSPlatform("windows")]
    internal static void UnregisterHotkey(nint window, int id)
    {
        if (window != 0)
        {
            _ = UnregisterHotKey(window, id);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);
}
