namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Categories of Azure Foundry / Azure OpenAI provider failures surfaced to callers. Mirrors the Codex
///     provider's <c>CodexProviderErrorKind</c> shape so the chat surface can translate cloud faults uniformly.
/// </summary>
public enum AzureFoundryProviderErrorKind
{
    /// <summary>The stored connection cannot build a client (bad endpoint, disallowed host, missing key).</summary>
    Configuration,

    /// <summary>Authentication / authorization failed (HTTP 401/403 — bad key, missing RBAC, expired token).</summary>
    AuthFailed,

    /// <summary>The request was blocked by the Azure content filter (HTTP 400 <c>content_filter</c>).</summary>
    ContentFiltered,

    /// <summary>A transport / backend error reaching the Azure endpoint.</summary>
    Transport,

    /// <summary>
    ///     No usable Entra ID sign-in is available yet (device-code / interactive-browser silent auth has no
    ///     persisted session for this connection). The operator must complete sign-in first.
    /// </summary>
    AuthRequired
}

/// <summary>
///     A typed Azure Foundry provider error. Messages must never contain the API key or an Entra token value
///     (sanitized at the translation boundary).
/// </summary>
public sealed class AzureFoundryProviderException : Exception
{
    public AzureFoundryProviderException(AzureFoundryProviderErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public AzureFoundryProviderException(AzureFoundryProviderErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The category of failure.</summary>
    public AzureFoundryProviderErrorKind Kind { get; }
}
