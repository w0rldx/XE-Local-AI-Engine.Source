namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     A single manually-added Azure Foundry deployment that surfaces in the chat model picker.
/// </summary>
/// <remarks>
///     No <c>required</c> members so a partial / legacy JSON parse never throws. Non-blank
///     <see cref="DeploymentName" /> is enforced by store validation, not by deserialization.
/// </remarks>
public sealed record StoredAzureFoundryModel
{
    /// <summary>
    ///     The Azure deployment name (as shown in the Foundry portal), used as the model id.
    /// </summary>
    public string DeploymentName { get; init; } = string.Empty;

    /// <summary>
    ///     Optional human-friendly label shown alongside the deployment name.
    /// </summary>
    public string? DisplayLabel { get; init; }
}
