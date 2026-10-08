namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>What one apply request did: scheduled the update, found nothing to apply, or held back for running work.</summary>
public sealed class AppUpdateApplyResult
{
    /// <summary>True when the update was downloaded and scheduled; the host stops once the response completes.</summary>
    public required bool Applying { get; init; }

    /// <summary>The version being installed, from the apply's own re-check; null unless <see cref="Applying" /> is true.</summary>
    public string? TargetVersion { get; init; }

    /// <summary>Work the update restart would stop. Non-empty only when the apply was refused because force was not set.</summary>
    public IReadOnlyList<AppUpdateBusyItem> BusyItems { get; init; } = [];
}
