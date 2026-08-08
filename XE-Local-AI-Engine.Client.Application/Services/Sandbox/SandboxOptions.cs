namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound selection of the AgentHome <see cref="ISandboxRuntimeProvider" />. The provider is resolved
///     once at startup as a singleton, so changing <see cref="Provider" /> requires a restart. There is intentionally
///     NO execution-capable code default: an unset provider resolves to the deterministic fake in non-Production, and
///     startup validation rejects an unset provider in Production (a stripped config must never silently grant the
///     host-command-executing provider). Bound from the <c>AgentHome:Sandbox</c> section.
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "AgentHome:Sandbox";

    /// <summary>
    ///     Selected provider name. When set, must match an <see cref="ISandboxRuntimeProvider.ProviderName" /> known to
    ///     DI. Null/blank means "unset" — fail-loud in Production, deterministic fake otherwise.
    /// </summary>
    public string? Provider { get; set; }
}
