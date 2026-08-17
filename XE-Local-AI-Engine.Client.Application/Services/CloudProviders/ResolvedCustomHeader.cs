namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     One custom request header after resolution from its <see cref="StoredAzureFoundryHeader" />: trimmed name, and a
///     value that is never null (a stored null collapses to empty). The value may be a secret, so it is never logged.
/// </summary>
internal sealed record ResolvedCustomHeader(string Name, string Value);
