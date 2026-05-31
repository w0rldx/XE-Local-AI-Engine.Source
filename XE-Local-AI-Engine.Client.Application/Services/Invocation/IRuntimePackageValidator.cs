namespace XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>
///     Startup/options validator for i runtime package settings.
/// </summary>
public interface IRuntimePackageValidator
{
    RuntimePackageValidationResult Validate(Models.RuntimePackage package);
}
