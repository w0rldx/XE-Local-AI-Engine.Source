namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>The engine's process affinity mask and priority class, read at each launch and applied to the still suspended command.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct WindowsJobScheduling
{
    public nuint AffinityMask { get; init; }

    public uint PriorityClass { get; init; }
}
