// The Desktop shell compiles this file as linked source under its own namespace (it has no engine references).
#if XE_DESKTOP
namespace XE_Local_AI_Engine.Desktop.Linux;
#else
namespace XE_Local_AI_Engine.Providers.Abstractions;
#endif

using System.Runtime.InteropServices;

/// <summary>What one <see cref="LinuxPidFd.Send" /> did, and the errno it failed with (0 once sent).</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LinuxPidFdSendResult(LinuxPidFdSendStatus Status, int Errno);
