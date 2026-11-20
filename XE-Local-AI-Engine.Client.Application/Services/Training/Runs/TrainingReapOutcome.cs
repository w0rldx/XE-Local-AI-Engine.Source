namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>Whether a reap proved that the process a receipt describes no longer runs; the default fails safe.</summary>
internal enum TrainingReapOutcome
{
    /// <summary>Still alive or unreadable after the bounded kill, or the reap failed: the receipt is the only trace left.</summary>
    NotVerified,

    /// <summary>No readable receipt, no matching live process, or the process was confirmed gone after the kill.</summary>
    Verified
}
