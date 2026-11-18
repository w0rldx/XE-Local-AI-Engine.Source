namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.InteropServices;

/// <summary>What one <see cref="LinuxPidFd.Send" /> did, and the errno it failed with (0 once sent).</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct LinuxPidFdSendResult(LinuxPidFdSendStatus Status, int Errno);
