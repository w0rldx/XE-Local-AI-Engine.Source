namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     A tool call needed a human approval in a run that structurally cannot obtain one — an UNATTENDED (scheduled,
///     headless) invocation, where nobody is watching the approval card. The runner throws this instead of registering
///     the request, broadcasting it and then waiting out the whole pending-approval window before failing with a
///     generic timeout: the outcome was already decided the moment the request was raised, so the only things the wait
///     bought were latency and a misleading audit row.
///     <para>
///         The message is fixed-shape and names the tool, so the operator's failed scheduled run says WHY it failed
///         ("approval required in an unattended run: load_skill") rather than "the operation timed out". It is surfaced
///         verbatim by the failure mapping, which is safe because it carries only a tool name — never arguments, model
///         output or skill content.
///     </para>
/// </summary>
public sealed class ApprovalUnavailableException : InvalidOperationException
{
    /// <summary>
    ///     The fixed prefix of the reason this exception carries. It is a constant rather than a literal at the throw
    ///     site because the runner CLASSIFIES this failure rather than letting it escape, and surfaces the reason
    ///     verbatim as the terminal error — so a caller that has to tell "this agent needs a capability it cannot have
    ///     unattended" apart from "something broke" has only the message to go on. One authority, so the two cannot
    ///     drift.
    /// </summary>
    public const string UnattendedReasonPrefix = "approval required in an unattended run: ";

    public ApprovalUnavailableException(string message)
        : base(message)
    {
    }
}
