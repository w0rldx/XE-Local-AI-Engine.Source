namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Public so tests can substitute it, matching <c>IDevelopmentRepositoryBindingService</c> and
///     <c>IDevelopmentCoordinator</c>: this assembly is not strong-named, so Castle DynamicProxy cannot proxy an
///     internal interface. The implementation stays internal.
/// </summary>
public interface IDevelopmentCommandProfileDetector
{
    DevelopmentProfileDetection Detect(string repositoryRoot);
}
