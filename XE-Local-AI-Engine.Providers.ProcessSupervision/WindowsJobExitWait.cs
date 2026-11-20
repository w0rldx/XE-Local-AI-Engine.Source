namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>What one bounded wait on a process handle saw: its exit, the deadline, or a failed wait.</summary>
internal enum WindowsJobExitWait
{
    Signalled,
    TimedOut,
    Failed
}
