namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Owner/node-scoped identity of an AgentHome sandbox (AgentHome plan §6.2, §1.1.4). Two attach keys are equal
///     when every field matches, so a provider can validate an attach request by value. The key intentionally
///     excludes conversation id and prompt text — it identifies the durable node-scoped sandbox, not a single run. A
///     change of <see cref="OwnerUserId" /> forbids reuse: the provider must kill and reinitialize.
/// </summary>
public sealed record SandboxAttachKey
{
    /// <summary>Owner of the AgentHome sandbox. Changing it forbids reuse of any prior sandbox or workspace contents.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>The node the sandbox belongs to.</summary>
    public required string NodeId { get; init; }

    /// <summary>The provider that owns the sandbox (matches <see cref="ISandboxRuntimeProvider.ProviderName" />).</summary>
    public required string ProviderName { get; init; }

    /// <summary>The runtime profile the sandbox was created with (e.g. <c>"dotnet-agent-home"</c>).</summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>The AgentHome manifest version in force when the sandbox was created.</summary>
    public required int ManifestVersion { get; init; }
}
