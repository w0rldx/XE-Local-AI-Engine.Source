namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

/// <summary>Owns a Win32 Job Object handle; closing the last one terminates a kill-on-close job's processes.</summary>
/// <remarks>Shared by <see cref="WindowsJobObjectProcessHandle" /> and <see cref="WindowsJobApi" />, so the handle type exists once.</remarks>
[SupportedOSPlatform("windows")]
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle()
    {
        return WindowsJobNative.CloseHandle(handle);
    }
}
