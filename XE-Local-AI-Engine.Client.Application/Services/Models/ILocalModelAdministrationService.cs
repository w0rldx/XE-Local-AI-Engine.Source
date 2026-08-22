namespace XE_Local_AI_Engine.Client.Services.Models;

public enum LocalModelSelectionPolicy
{
    ConfiguredModel = 0,
    InstalledLocalOnly = 1
}

public static class LocalModelAdministrationFailureCodes
{
    public const string InvalidModelName = "invalid_model_name";
    public const string ModelNotInstalled = "model_not_installed";
}

public sealed record LocalModelDeletionResult(
    bool Succeeded,
    string? ModelName,
    bool Deleted,
    string? FailureCode = null,
    string? DisplayMessage = null);

public sealed record LocalModelSelectionResult(
    bool Succeeded,
    string? SelectedModelName,
    string? PreviousModelName,
    string? FailureCode = null,
    string? DisplayMessage = null);

/// <summary>Transport-neutral local-model deletion and default-selection application boundary.</summary>
public interface ILocalModelAdministrationService
{
    Task<LocalModelDeletionResult> DeleteAsync(string? modelName, CancellationToken cancellationToken = default);

    Task<LocalModelSelectionResult> SelectDefaultAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken = default);
}
