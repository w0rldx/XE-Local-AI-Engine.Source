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
    public ApprovalUnavailableException(string message)
        : base(message)
    {
    }
}
