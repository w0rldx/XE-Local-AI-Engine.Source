namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// Coordinates the Operator-facing Codex login lifecycle: start (or supersede) a loopback PKCE login that
/// returns the authorize URL immediately, and expose a pollable status while the exchange completes in the
/// background (plan §8). Distinct from <see cref="ICodexAuthService"/>, which owns the per-attempt OAuth
/// mechanics; this owns the singleton pending-login state shared across requests.
/// </summary>
public interface ICodexLoginCoordinator
{
    /// <summary>
    /// Starts a loopback PKCE login, superseding any in-flight attempt, and returns the authorize URL the
    /// operator opens in a browser. The token exchange completes in the background; poll <see cref="GetStatus"/>.
    /// </summary>
    Uri Start();

    /// <summary>Returns the current login status snapshot. Carries no token material.</summary>
    CodexLoginStatus GetStatus();
}
