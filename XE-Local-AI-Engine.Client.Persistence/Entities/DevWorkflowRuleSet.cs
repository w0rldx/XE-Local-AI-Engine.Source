namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A scoped markdown document the resolver injects into a node's context. Not a rule TABLE: v1's single job is
///     context injection, so the rule set is the granular unit — which is also what makes "which rules applied"
///     answerable from one recorded id.
/// </summary>
internal sealed class DevWorkflowRuleSet
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>What this rule set is for. Plaintext: it is a label, and the list page renders it without the body.</summary>
    public string? Description { get; set; }

    /// <summary>
    ///     <c>{projectIds[], nodeTypes[]}</c>, an EMPTY axis meaning "matches everything". Plaintext because it is
    ///     structural — ids and closed tokens — and the resolver reads it on every materialization.
    /// </summary>
    public string ScopeJson { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>The markdown itself. Same sensitivity class as an agent definition's instructions, so it is encrypted.</summary>
    public byte[] Body { get; set; } = [];

    /// <summary>
    ///     SHA-256 of the body bytes. It is what a node-run's policy resolution records, so an audit can prove WHICH
    ///     text applied without copying the body onto every node-run — and can tell that the current document is no
    ///     longer that text.
    /// </summary>
    public string ContentSha256 { get; set; } = string.Empty;

    public int Version { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}
