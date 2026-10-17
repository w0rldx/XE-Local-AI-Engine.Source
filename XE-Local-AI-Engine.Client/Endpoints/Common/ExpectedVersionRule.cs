namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using System.Linq.Expressions;
using FluentValidation;

/// <summary>
///     A missing version field binds to 0, which no stored row ever has (every versioned row starts at 1), so a
///     versioned write used to answer a misleading 409 VersionConflict. It is a 400 naming the field instead.
/// </summary>
/// <remarks>
///     The names differ on purpose: <c>expectedVersion</c> is the version of the row the call writes;
///     <c>expectedProjectVersion</c> is the benchmark project's, for a call that adds an item or starts runs in it.
/// </remarks>
internal static class ExpectedVersionRule
{
    public static void Require<T>(AbstractValidator<T> validator, Expression<Func<T, long>> selector, string field, string row)
    {
        _ = validator.RuleFor(selector)
                     .GreaterThan(0)
                     .WithMessage($"{field} is required: send the {row}'s current version.")
                     .OverridePropertyName(field);
    }
}
