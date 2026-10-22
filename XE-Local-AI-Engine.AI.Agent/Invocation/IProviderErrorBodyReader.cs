namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Reads the HTTP status and raw response body a provider SDK failure carries. The host implements it because the
///     agent layer references no provider SDK.
/// </summary>
public interface IProviderErrorBodyReader
{
    /// <summary>The first provider status failure in <paramref name="exception" />'s chain, or null when it holds none.</summary>
    ProviderErrorBody? Read(Exception exception);
}
