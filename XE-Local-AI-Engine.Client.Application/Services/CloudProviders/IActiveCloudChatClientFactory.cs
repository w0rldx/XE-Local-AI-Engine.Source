namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.AI;

/// <summary>
/// Resolves the <em>currently active</em> cloud chat client on demand (plan §0/C2, §7.2). The node's selected
/// cloud provider is the persisted <see cref="StoredCloudCredentials.ProviderName"/>; for Codex the live OAuth
/// session lives in a separate encrypted store, so selection consults Codex-session presence rather than the
/// Azure-shaped credential fields.
///
/// <para>
/// This exists so cloud-vs-local routing can be re-evaluated <b>per send</b> instead of being captured once at
/// startup: signing in (or out) at runtime takes effect on the next send without a node restart.
/// </para>
/// </summary>
public interface IActiveCloudChatClientFactory
{
    /// <summary>
    /// Attempts to build the active cloud chat client for the current selection.
    /// </summary>
    /// <param name="client">
    /// The provider-neutral cloud <see cref="IChatClient"/> when a cloud provider is selected and usable;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a cloud client was produced (caller should route to it); <see langword="false"/>
    /// when no cloud provider is selected (caller should route to the local model).
    /// </returns>
    /// <remarks>
    /// When a cloud provider IS selected but is not usable (e.g. Codex is selected but there is no valid session),
    /// this throws a typed provider error (surfaced as a re-authenticate prompt) rather than returning
    /// <see langword="false"/> — selecting a cloud provider must not silently fall back to the local model.
    /// </remarks>
    bool TryCreateActiveCloudChatClient(out IChatClient? client);

    /// <summary>
    /// Reports whether a cloud provider is currently selected (regardless of whether it is presently usable).
    /// Cheap; performs no network I/O. Used for capability-gating and routing decisions.
    /// </summary>
    bool IsCloudProviderSelected();

    /// <summary>
    /// Invalidates the in-memory selection snapshot so the next resolution re-reads the encrypted token / credential
    /// store. Called on sign-out (the logout endpoint) and on sign-in (the login coordinator's success callback,
    /// once the background token exchange persists the session) so either takes effect on the very next send instead
    /// of waiting for the short snapshot TTL to lapse.
    /// </summary>
    void InvalidateSelectionCache();
}
