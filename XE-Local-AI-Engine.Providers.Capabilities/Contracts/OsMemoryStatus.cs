namespace XE_Local_AI_Engine.Providers.Capabilities.Contracts;

using System.Runtime.InteropServices;

/// <summary>A reading of total and available physical RAM, in bytes.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct OsMemoryStatus(long TotalBytes, long AvailableBytes);
