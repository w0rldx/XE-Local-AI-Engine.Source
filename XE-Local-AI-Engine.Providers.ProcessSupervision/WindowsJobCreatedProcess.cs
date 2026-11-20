namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>The process and thread handles one <c>CreateProcessW</c> returned, owned by whoever holds this until it hands them on.</summary>
internal sealed class WindowsJobCreatedProcess
{
    public required SafeHandle Process { get; init; }

    public required SafeHandle Thread { get; init; }

    public required int ProcessId { get; init; }
}
