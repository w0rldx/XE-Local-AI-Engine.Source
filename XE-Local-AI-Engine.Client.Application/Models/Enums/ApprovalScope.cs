namespace XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     How long an operator's APPROVE decision on a tool-approval request lasts. Rides the loopback resolve contract
///     (<c>ResolveToolApprovalRequest</c>) and the Application-internal dispatcher only — deliberately NOT on the
///     cross-repo <c>ApprovalResolvedEvent</c> SignalR contract, because session scope is a loopback-only concept the
///     platform hub knows nothing about.
///     <para>
///         A DENY is never remembered, whatever the scope: the memo only ever suppresses a prompt the operator already
///         answered with "yes", so forgetting it can add prompts but never remove one.
///     </para>
/// </summary>
public enum ApprovalScope
{
    /// <summary>Approves this call only — the unchanged default, applied when a caller sends no scope at all.</summary>
    Once = 0,

    /// <summary>
    ///     Remembers the approval for the rest of the CONVERSATION (not the browser session, not the node): the same
    ///     skill tool, on the same skill at the same content version, for the same resource, stops prompting. Held in
    ///     memory on the runner and never persisted, so a node restart forgets it.
    /// </summary>
    Session = 1
}
