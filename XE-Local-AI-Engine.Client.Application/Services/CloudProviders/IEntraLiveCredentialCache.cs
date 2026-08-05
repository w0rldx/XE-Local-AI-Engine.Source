namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Azure.Core;

/// <summary>
///     Holds the single live, already-authenticated Entra ID device-code <see cref="TokenCredential" /> for the
///     process lifetime so a chat send reuses the exact credential the sign-in coordinator just authenticated,
///     rather than rebuilding a fresh <see cref="Azure.Identity.DeviceCodeCredential" /> from only the persisted
///     <see cref="Azure.Identity.AuthenticationRecord" /> — whose backing MSAL token cache may not have survived
///     (e.g. no OS-native encrypted persistence on this platform), leaving nothing to
///     silently refresh from. A single slot matches the single-connection cloud-credential model: a new sign-in or
///     a settings change simply overwrites it, and a process restart always starts empty (silent auth then depends
///     on OS-native persistence, or surfaces <c>AuthRequired</c> — the accepted degraded behavior).
/// </summary>
public interface IEntraLiveCredentialCache
{
    /// <summary>Returns the cached credential only when <paramref name="key" /> matches the one it was stored under.</summary>
    TokenCredential? TryGet(string key);

    /// <summary>Replaces the single cached slot.</summary>
    void Store(string key, TokenCredential credential);
}
