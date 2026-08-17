namespace XE_Local_AI_Engine.Client.Common;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
///     Best-effort termination of a host child process (the <c>git</c> invocations behind AgentHome and Development
///     Mode) after a timeout or a failure, where the kill is cleanup and the original outcome is what the caller
///     reports.
/// </summary>
internal static class ProcessTermination
{
    /// <summary>
    ///     Tree-kills <paramref name="process" />, swallowing only the two outcomes that mean "there is nothing left to
    ///     kill or nothing more we can do": the process exited between the decision and the kill
    ///     (<see cref="InvalidOperationException" />) and the OS refusing the signal (<see cref="Win32Exception" />).
    ///     Every other exception still surfaces — a kill failing for an unexpected reason is not cleanup noise.
    /// </summary>
    public static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception)
        {
            // Could not signal the process; nothing more to do.
        }
    }
}
