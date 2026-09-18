namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;

/// <summary>
///     The tool-catalog endpoint's only door to the node's tool catalog and to the node approval policy, so the badge an
///     operator sees is computed by the same policy runtime enforcement applies rather than by a second rule at the HTTP
///     edge.
///     <para>
///         The composition lives in its own type because <see cref="ILocalToolOfferProvider" /> deliberately consults no
///         <see cref="IToolApprovalPolicy" /> anywhere (see that interface's own documentation): the raw declared flag is
///         what it returns, and each caller composes it. This type is that composition for the catalog read, and it keeps
///         the endpoint from having to take the <c>AI.Agent</c> policy contract itself.
///     </para>
/// </summary>
public sealed class ToolCatalogService
{
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly IToolApprovalPolicy _approvalPolicy;

    public ToolCatalogService(ILocalToolOfferProvider localToolOfferProvider, IToolApprovalPolicy approvalPolicy)
    {
        ArgumentNullException.ThrowIfNull(localToolOfferProvider);
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        _localToolOfferProvider = localToolOfferProvider;
        _approvalPolicy = approvalPolicy;
    }

    /// <summary>
    ///     The full tool catalog as rich entries, ungated by model capability:
    ///     <see cref="ILocalToolOfferProvider.GetKnownToolsAsync" /> verbatim.
    /// </summary>
    public Task<IReadOnlyList<LocalToolCatalogEntry>> GetKnownToolsAsync(CancellationToken cancellationToken = default)
    {
        return _localToolOfferProvider.GetKnownToolsAsync(cancellationToken);
    }

    /// <summary>
    ///     The node policy's effective approval for <paramref name="entry" />, composed on top of the entry's own
    ///     declared flag: <see cref="IToolApprovalPolicy.RequiresApproval" /> applied to the entry's name, category and
    ///     catalog default.
    /// </summary>
    public bool RequiresApproval(LocalToolCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return _approvalPolicy.RequiresApproval(entry.Name, entry.Category, entry.RequiresApproval);
    }

    /// <summary>
    ///     Whether a session-scoped approval can ever be remembered for <paramref name="entry" />, with the node's
    ///     always-prompt switch applied: <see cref="SessionApprovalEligibility.IsToolEligible(IToolApprovalPolicy, string, bool)" />
    ///     for the entry's name and custom-tool mode.
    /// </summary>
    public bool IsSessionScopeEligible(LocalToolCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return SessionApprovalEligibility.IsToolEligible(_approvalPolicy, entry.Name, entry.IsFixedCustomTool);
    }
}
