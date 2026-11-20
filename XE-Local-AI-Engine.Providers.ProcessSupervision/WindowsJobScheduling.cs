namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>The engine's process and system affinity masks and its priority class, read at each launch for the still suspended command.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct WindowsJobScheduling
{
    public nuint AffinityMask { get; init; }

    /// <summary>The system mask the same read returns; an engine mask equal to it means the engine is unrestricted.</summary>
    public nuint SystemAffinityMask { get; init; }

    public uint PriorityClass { get; init; }
}
