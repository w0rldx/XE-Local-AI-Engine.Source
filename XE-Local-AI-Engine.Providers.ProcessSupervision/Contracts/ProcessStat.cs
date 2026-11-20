namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

using System.Runtime.InteropServices;

/// <summary>The identity fields of one process as <c>/proc/[pid]/stat</c> reports them; <see cref="StartTicks" /> survives exec and changes on pid reuse.</summary>
/// <param name="Exited">State Z (zombie) or X (dead): the process runs nothing and holds no memory; only its parent's wait is left.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProcessStat(int ParentProcessId, int ProcessGroupId, long StartTicks, bool Exited = false);
