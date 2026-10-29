namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Diagnostics.CodeAnalysis;

public enum LocalModelSelectionPolicy
{
    ConfiguredModel = 0,
    InstalledLocalOnly = 1
}

public static class LocalModelAdministrationFailureCodes
{
    public const string InvalidModelName = "invalid_model_name";
    public const string ModelNotInstalled = "model_not_installed";
    public const string ModelKindNotChat = "model_kind_not_chat";
}

public sealed class LocalModelDeletionResult
{
    /// <summary>True when the name was valid; the delete then names <see cref="ModelName" />, whether or not it removed anything.</summary>
    [MemberNotNullWhen(true, nameof(ModelName))]
    public required bool Succeeded { get; init; }

    public required string? ModelName { get; init; }

    public required bool Deleted { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

public sealed class LocalModelSelectionResult
{
    [MemberNotNullWhen(true, nameof(SelectedModelName))]
    public required bool Succeeded { get; init; }

    public required string? SelectedModelName { get; init; }

    public required string? PreviousModelName { get; init; }

    public string? FailureCode { get; init; }

    public string? DisplayMessage { get; init; }
}

/// <summary>Transport-neutral local-model deletion and default-selection application boundary.</summary>
public interface ILocalModelAdministrationService
{
    Task<LocalModelDeletionResult> DeleteAsync(string? modelName, CancellationToken cancellationToken = default);

    Task<LocalModelSelectionResult> SelectDefaultAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken = default);
}
