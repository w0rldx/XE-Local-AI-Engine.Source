namespace XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     The accept path: resolve the trigger by name, authorise the key against it, dedup, admit under a hard bound, and
///     enqueue for the coordinator.
///     <para>
///         <b>Admission commits FIRST, then the conversation and the seed.</b> The store's own <c>AcceptAsync</c> is a
///         hard reservation under <c>BEGIN IMMEDIATE</c>, so a queue-full, revoked, duplicate or conflicting request
///         writes nothing at all — no conversation, no message, no execution row — and no orphan conversation can
///         exist. A failure AFTER the commit runs forward instead of backward: the row stays <c>Accepted</c> and the
///         coordinator terminalises it with a real reason, which is visible, cancellable and audited.
///     </para>
/// </summary>
public interface IIntegrationInvocationService
{
    Task<IntegrationAcceptResult> AcceptAsync(IntegrationAcceptRequest request, CancellationToken cancellationToken = default);
}
