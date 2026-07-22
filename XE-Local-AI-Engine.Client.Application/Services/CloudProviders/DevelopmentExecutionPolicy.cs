namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Declares whether a Development attempt must remain node-local or may use a curated cloud bundle.
/// </summary>
public enum DevelopmentExecutionPolicy
{
    LocalOnly = 0,
    CloudScoped = 1
}
