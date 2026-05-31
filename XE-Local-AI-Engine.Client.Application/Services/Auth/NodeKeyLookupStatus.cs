namespace XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Enumerates supported node key lookup status values.
/// </summary>
public enum NodeKeyLookupStatus
{
    Active = 0,
    Retired = 1,
    RetiredExpired = 2,
    Missing = 3
}
