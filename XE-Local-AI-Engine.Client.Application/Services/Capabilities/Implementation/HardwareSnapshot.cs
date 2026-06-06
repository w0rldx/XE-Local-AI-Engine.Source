namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

/// <summary>Local hardware facts gathered once per capability report.</summary>
/// <param name="RamMb">Total system RAM in MB, when detectable.</param>
/// <param name="GpuInfo">Primary GPU facts, or <c>null</c> when no GPU was detected.</param>
/// <param name="CpuClass">Human-readable CPU description (model + logical core count).</param>
internal sealed record HardwareSnapshot(long? RamMb, GpuInfo? GpuInfo, string? CpuClass);
