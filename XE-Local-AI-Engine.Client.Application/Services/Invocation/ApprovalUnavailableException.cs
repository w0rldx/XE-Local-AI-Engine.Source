namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     A tool call needed a human approval in a run that structurally cannot obtain one: an UNATTENDED, headless
///     invocation where nobody is watching the approval card.
/// </summary>
/// <remarks>
///     The runner throws this instead of registering the request, broadcasting it and waiting out the whole
///     pending-approval window before failing with a generic timeout — the outcome was decided the moment the request
///     was raised, so the wait buys only latency and a misleading audit row. The message is fixed-shape and names the
///     tool, so a failed scheduled run says WHY, and the failure mapping surfaces it verbatim, which is safe because it
///     carries a tool name and never arguments, model output or skill content.
/// </remarks>
public sealed class ApprovalUnavailableException : InvalidOperationException
{
    /// <summary>The fixed prefix of the reason this exception carries.</summary>
    /// <remarks>
    ///     A constant rather than a literal at the throw site because the runner CLASSIFIES this failure rather than
    ///     letting it escape, and surfaces the reason verbatim as the terminal error — so a caller telling "this agent
    ///     needs a capability it cannot have unattended" apart from "something broke" has only the message to go on.
    ///     One authority, so the two cannot drift.
    /// </remarks>
    public const string UnattendedReasonPrefix = "approval required in an unattended run: ";

    public ApprovalUnavailableException(string message)
        : base(message)
    {
    }
}
