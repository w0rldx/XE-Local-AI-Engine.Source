namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Turns the raw audio sessions a host enumerated into the rows the picker shows. Pure, and deliberately free of
///     any WASAPI type, so the aggregation runs — and is tested — on every operating system.
/// </summary>
/// <remarks>
///     Enumerating the sessions in the first place is the Windows-only half and lives in
///     <see cref="WindowsProcessAudioCaptureSource" />.
/// </remarks>
internal static class ProcessAudioCaptureCandidates
{
    /// <summary>
    ///     De-duplicates by process id, <b>OR-ing</b> audio activity across that process's sessions, and orders by
    ///     name.
    /// </summary>
    /// <remarks>
    ///     One application playing to two endpoints, or holding one idle session beside a playing one, enumerates
    ///     more than once. Keeping the first row seen made the answer depend on enumeration order: an inactive
    ///     session encountered first reported a playing application as silent. Activity is a property of the
    ///     process, not of whichever of its sessions happened to come back first.
    /// </remarks>
    /// <param name="sessions">One entry per enumerated session: its process id, and whether that session is active.</param>
    /// <param name="resolveName">Resolves a display name, called once per distinct process id.</param>
    internal static IReadOnlyList<ProcessAudioCaptureCandidate> Aggregate(IEnumerable<(int ProcessId, bool Active)> sessions,
        Func<int, string> resolveName)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(resolveName);

        var activeByProcessId = new Dictionary<int, bool>();
        foreach (var (processId, active) in sessions)
        {
            // A zero or negative id is not a process capture could ever target: WithProcessLoopback takes a uint.
            if (processId <= 0)
            {
                continue;
            }

            activeByProcessId[processId] = activeByProcessId.TryGetValue(processId, out var seen) ? seen || active : active;
        }

        return
        [
            .. activeByProcessId
               .Select(entry => new ProcessAudioCaptureCandidate
               {
                   ProcessId = entry.Key,
                   Name = resolveName(entry.Key),
                   HasAudio = entry.Value
               })
               .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
               .ThenBy(candidate => candidate.ProcessId)
        ];
    }
}
