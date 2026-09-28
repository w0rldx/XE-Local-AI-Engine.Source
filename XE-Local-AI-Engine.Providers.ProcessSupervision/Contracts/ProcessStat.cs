namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

using System.Runtime.InteropServices;

/// <summary>The identity fields of one live process as <c>/proc/[pid]/stat</c> reports them; <see cref="StartTicks" /> survives exec and changes on pid reuse.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ProcessStat(int ParentProcessId, int ProcessGroupId, long StartTicks);
