namespace XE_Local_AI_Engine.Client.Services.Invocation;

public interface IRuntimePackageValidator
{
    RuntimePackageValidationResult Validate(Models.RuntimePackage package);
}
