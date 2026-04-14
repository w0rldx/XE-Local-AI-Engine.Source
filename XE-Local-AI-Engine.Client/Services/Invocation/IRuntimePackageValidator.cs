namespace XE_Local_AI_Engine.Client.Services.Invocation;

using XE_Local_AI_Engine.Client.Models;

public interface IRuntimePackageValidator
{
    RuntimePackageValidationResult Validate(RuntimePackage package);
}
