namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// Raised for Codex OAuth login / refresh failures. Messages must never contain token values,
/// authorization codes, or response bodies that might echo them (plan §9).
/// </summary>
public sealed class CodexAuthException : Exception
{
    public CodexAuthException()
    {
    }

    public CodexAuthException(string message)
        : base(message)
    {
    }

    public CodexAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
