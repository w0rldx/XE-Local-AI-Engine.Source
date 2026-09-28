namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>A candidate runtime process: its OS pid and resolved executable path (<see langword="null" /> when unresolved).</summary>
public readonly record struct StaleProcess(int Pid, string? ExecutablePath);
