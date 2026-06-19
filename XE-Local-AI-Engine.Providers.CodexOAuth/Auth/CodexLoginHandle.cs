namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// A pending Codex OAuth login: the <see cref="AuthorizeUrl"/> is available immediately (so the endpoint can
/// hand it to the React UI as a copyable/clickable link), while <see cref="Completion"/> resolves
/// once the loopback callback delivers the authorization code and the token exchange persists the session.
/// </summary>
/// <param name="AuthorizeUrl">The PKCE authorize URL the operator opens in a browser. Contains no secrets.</param>
/// <param name="Completion">
/// Resolves with the persisted <see cref="CodexTokens"/> on success, or faults with a <see cref="CodexAuthException"/>
/// (or <see cref="OperationCanceledException"/> on timeout/supersede). Never logs token material.
/// </param>
public sealed record CodexLoginHandle(Uri AuthorizeUrl, Task<CodexTokens> Completion);
