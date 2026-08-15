namespace XE_Local_AI_Engine.Tests.Options;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Table-driven coverage for the options classes whose ONLY startup validation is data annotations — each is
///     registered with <c>.ValidateDataAnnotations().ValidateOnStart()</c> and has no hand-written
///     <c>IValidateOptions&lt;T&gt;</c> behind it, so the attributes are the whole gate.
/// </summary>
/// <remarks>
///     For every annotated property the bound is probed from both sides: one step outside must be rejected and the
///     boundary value itself must be accepted. That is what makes this catch the two silent regressions an
///     attribute-shaped gate actually suffers — a bound quietly widened, or an attribute dropped entirely during a
///     refactor. Both compile, both pass every other suite, and both only show up as an operator configuration that starts when
///     it should have refused.
/// </remarks>
public sealed class AnnotatedOptionsBoundsTests
{
    [Test]
    [Arguments(typeof(LocalChatAgentOptions))]
    [Arguments(typeof(InvocationAgentOptions))]
    [Arguments(typeof(OrchestrationAgentOptions))]
    [Arguments(typeof(AgentToolPipelineOptions))]
    [Arguments(typeof(ProviderCallBudgetOptions))]
    [Arguments(typeof(ChatRetentionOptions))]
    [Arguments(typeof(ChatStreamBudgetOptions))]
    [Arguments(typeof(CodexOptions))]
    [Arguments(typeof(NodeAuthOptions))]
    [Arguments(typeof(NodeJwtOptions))]
    [Arguments(typeof(DevelopmentOptions))]
    [Arguments(typeof(ConversationCompactionOptions))]
    [Arguments(typeof(ContainerSandboxOptions))]
    public void DefaultOptions_PassDataAnnotationValidation(Type optionsType)
    {
        // The shipped defaults must always start a node; a bound tightened past its own default would otherwise only
        // be discovered by an operator on an upgrade.
        var errors = Validate(Create(optionsType));

        AssertEx.Empty(errors.Select(static error => error.ErrorMessage));
    }

    [Test]
    [Arguments(typeof(LocalChatAgentOptions))]
    [Arguments(typeof(OrchestrationAgentOptions))]
    [Arguments(typeof(AgentToolPipelineOptions))]
    [Arguments(typeof(ProviderCallBudgetOptions))]
    [Arguments(typeof(ChatRetentionOptions))]
    [Arguments(typeof(ChatStreamBudgetOptions))]
    [Arguments(typeof(NodeAuthOptions))]
    [Arguments(typeof(NodeJwtOptions))]
    [Arguments(typeof(DevelopmentOptions))]
    [Arguments(typeof(ConversationCompactionOptions))]
    [Arguments(typeof(ContainerSandboxOptions))]
    public void EveryAnnotatedRange_RejectsJustOutsideAndAcceptsTheBoundary(Type optionsType)
    {
        var probed = 0;
        foreach (var property in Writable(optionsType))
        {
            if (property.GetCustomAttribute<RangeAttribute>() is not { } range)
            {
                continue;
            }

            probed++;
            AssertBoundary(optionsType, property, ToDecimal(range.Minimum), isMinimum: true);
            AssertBoundary(optionsType, property, ToDecimal(range.Maximum), isMinimum: false);
        }

        AssertEx.True(probed > 0, $"{optionsType.Name} is listed here as range-annotated but declares no [Range] property.");
    }

    [Test]
    [Arguments(typeof(LocalChatAgentOptions))]
    [Arguments(typeof(InvocationAgentOptions))]
    [Arguments(typeof(CodexOptions))]
    [Arguments(typeof(NodeJwtOptions))]
    [Arguments(typeof(ContainerSandboxOptions))]
    public void EveryRequiredStringOption_RejectsNullAndBlank(Type optionsType)
    {
        var probed = 0;
        foreach (var property in Writable(optionsType))
        {
            if (property.GetCustomAttribute<RequiredAttribute>() is null || property.PropertyType != typeof(string))
            {
                continue;
            }

            probed++;
            foreach (var missing in new string?[] { null, string.Empty })
            {
                var instance = Create(optionsType);
                property.SetValue(instance, missing);

                AssertEx.Contains(Validate(instance),
                    error => error.MemberNames.Contains(property.Name, StringComparer.Ordinal),
                    $"{optionsType.Name}.{property.Name} is [Required] but a missing value validated.");
            }
        }

        AssertEx.True(probed > 0, $"{optionsType.Name} is listed here as carrying [Required] strings but declares none.");
    }

    [Test]
    public void ContainerSandboxMinimumApiVersion_IsPinnedToAMajorMinorPattern()
    {
        // The one regular-expression annotation in the set; a loosened pattern would let a malformed version through to
        // the daemon probe, where it fails much later and far less legibly.
        var instance = new ContainerSandboxOptions
        {
            MinimumApiVersion = "1.41.0"
        };

        AssertEx.Contains(Validate(instance),
            error => error.MemberNames.Contains(nameof(ContainerSandboxOptions.MinimumApiVersion), StringComparer.Ordinal));
    }

    private static void AssertBoundary(Type optionsType, PropertyInfo property, decimal? bound, bool isMinimum)
    {
        if (bound is not { } boundary)
        {
            return;
        }

        var outside = isMinimum ? boundary - 1 : boundary + 1;
        if (TryCreateWith(optionsType, property, outside) is { } outsideInstance)
        {
            AssertEx.Contains(Validate(outsideInstance),
                error => error.MemberNames.Contains(property.Name, StringComparer.Ordinal),
                $"{optionsType.Name}.{property.Name} accepted {outside}, which is outside its declared [Range].");
        }

        var atBoundary = AssertEx.NotNull(TryCreateWith(optionsType, property, boundary),
            $"{optionsType.Name}.{property.Name} could not be set to its own declared bound {boundary}.");

        AssertEx.False(Validate(atBoundary).Any(error => error.MemberNames.Contains(property.Name, StringComparer.Ordinal)),
            $"{optionsType.Name}.{property.Name} rejected {boundary}, which its own [Range] declares as valid.");
    }

    /// <summary>Returns an instance with the property set, or null when the value does not fit the property's type.</summary>
    private static object? TryCreateWith(Type optionsType, PropertyInfo property, decimal value)
    {
        object converted;
        try
        {
            converted = Convert.ChangeType(value, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            // A bound sitting on the type's own limit has no "one step outside"; nothing to probe.
            return null;
        }

        var instance = Create(optionsType);
        property.SetValue(instance, converted);
        return instance;
    }

    private static decimal? ToDecimal(object? bound) =>
        bound switch
        {
            int value => value,
            long value => value,
            double value when value is >= (double)decimal.MinValue and <= (double)decimal.MaxValue => (decimal)value,
            _ => null
        };

    private static IEnumerable<PropertyInfo> Writable(Type optionsType) =>
        optionsType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(static property => property.CanWrite);

    private static object Create(Type optionsType) =>
        Activator.CreateInstance(optionsType) ?? throw new InvalidOperationException($"{optionsType.Name} has no parameterless constructor.");

    private static IReadOnlyList<ValidationResult> Validate(object instance)
    {
        var results = new List<ValidationResult>();
        _ = Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);
        return results;
    }
}
