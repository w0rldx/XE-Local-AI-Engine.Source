namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

/// <summary>
///     Resolves the <em>currently active</em> cloud chat client on demand, honoring the per-request model id when the
///     caller has one.
/// </summary>
/// <remarks>
///     The node's selected cloud provider is the persisted <see cref="StoredCloudCredentials.ProviderName" />; for
///     Codex the live OAuth session lives in a separate encrypted store, so selection consults Codex-session presence
///     rather than the Azure-shaped credential fields. Cloud-vs-local routing is re-evaluated <b>per send</b> instead
///     of being captured once at startup: signing in or out at runtime takes effect on the next send without a node
///     restart, AND picking a different model in the chat model dropdown takes effect on that same send.
/// </remarks>
public interface IActiveCloudChatClientFactory
{
    /// <summary>Attempts to build the active cloud chat client for <paramref name="requestedModelId" />, or for the node-default selection when it is <see langword="null" />/blank.</summary>
    /// <param name="requestedModelId">
    ///     The model id the caller asked for on this send (<c>ChatOptions.ModelId</c>): an Azure deployment name, a
    ///     Codex model id, a local model name, or <see langword="null" /> for none.
    /// </param>
    /// <param name="client">The cloud <see cref="IChatClient" /> when one is selected and usable; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when a cloud client was produced; <see langword="false" /> when the caller should route to the local model.</returns>
    /// <remarks>
    ///     A concrete id lets one send pick a DIFFERENT provider than the node default: selecting an Azure deployment
    ///     in the chat model dropdown routes to Azure even while the node default (or an active Codex session) would
    ///     route elsewhere. When a cloud provider IS selected but is not usable (Codex selected with no valid session)
    ///     this throws a typed provider error, surfaced as a re-authenticate prompt, rather than returning
    ///     <see langword="false" />: selecting a cloud provider must not silently fall back to the local model.
    /// </remarks>
    bool TryCreateActiveCloudChatClient(string? requestedModelId, out IChatClient? client);

    /// <summary>
    ///     Reports whether a cloud provider is currently selected for <paramref name="requestedModelId" />, regardless
    ///     of whether it is presently usable.
    /// </summary>
    /// <remarks>
    ///     Cheap: no network I/O beyond the short-TTL selection snapshot. Used for capability-gating and admission
    ///     (capacity) routing decisions.
    /// </remarks>
    /// <param name="requestedModelId">
    ///     The specific model id the caller is deciding about, or <see langword="null" /> to fall back to the
    ///     node-default selection (the pre-existing, caller-omits-a-model semantics).
    /// </param>
    bool IsCloudProviderSelected(string? requestedModelId = null);

    /// <summary>
    ///     Returns the fine-grained cloud provider name currently selected for <paramref name="requestedModelId" />
    ///     (<c>codex</c> or <c>azure</c>), or <see langword="null" /> when none is selected and the caller routes local.
    /// </summary>
    /// <remarks>
    ///     Like <see cref="IsCloudProviderSelected" /> it reports selection regardless of present usability, and is
    ///     cheap — no network I/O beyond the short-TTL selection snapshot. Used by the usage ledger to attribute a
    ///     turn's tokens to the provider that served it.
    /// </remarks>
    /// <param name="requestedModelId">
    ///     The specific model id the caller is attributing, or <see langword="null" /> to fall back to the node-default
    ///     selection (the caller-omits-a-model semantics).
    /// </param>
    string? ResolveActiveCloudProviderName(string? requestedModelId = null);

    /// <summary>
    ///     Invalidates the in-memory selection snapshot so the next resolution re-reads the encrypted token /
    ///     credential store.
    /// </summary>
    /// <remarks>
    ///     Called on sign-out (the logout endpoint) and on sign-in (the login coordinator's success callback, once the
    ///     background token exchange persists the session), so either takes effect on the very next send instead of
    ///     waiting for the short snapshot TTL to lapse.
    /// </remarks>
    void InvalidateSelectionCache();
}
