namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Runtime.InteropServices;

/// <summary>Process-group id and start time as read from <c>/proc/[pid]/stat</c>.</summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct TrainingProcessStat(int Pgid, long StartTicks);
