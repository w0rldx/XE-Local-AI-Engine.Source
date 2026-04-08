namespace XE_Local_AI_Engine.Services.Invocation
{
    using System;
    using System.Collections.Generic;
    using XE_Local_AI_Engine.Models;

    public interface IRuntimePackageValidator
    {
        RuntimePackageValidationResult Validate(RuntimePackage package);
    }

    public sealed record RuntimePackageValidationResult
    {
        public RuntimePackageValidationResult(bool isValid, IReadOnlyList<string> errors)
        {
            IsValid = isValid;
            Errors = errors;
        }

        public bool IsValid { get; }

        public IReadOnlyList<string> Errors { get; }

        public static RuntimePackageValidationResult Success { get; } = new(true, Array.Empty<string>());

        public static RuntimePackageValidationResult FromErrors(IReadOnlyList<string> errors)
        {
            return errors.Count == 0 ? Success : new RuntimePackageValidationResult(false, errors);
        }
    }
}
