namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Globalization;
using System.Reflection;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared source-build base reports its phases and outcomes as its own enums, and each runtime casts them to its
///     public contract enum; a member added on one side only would silently report the wrong value.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ManagedSourceBuildEnumParityTests
{
    [Test]
    [Arguments(typeof(WhisperCppSourceBuildService), "BuildPhase", typeof(WhisperCppSourceBuildPhase))]
    [Arguments(typeof(StableDiffusionCppSourceBuildService), "BuildPhase", typeof(StableDiffusionCppSourceBuildPhase))]
    [Arguments(typeof(WhisperCppSourceBuildService), "StartOutcome", typeof(WhisperCppSourceBuildStartOutcome))]
    [Arguments(typeof(StableDiffusionCppSourceBuildService), "StartOutcome", typeof(StableDiffusionCppSourceBuildStartOutcome))]
    [Arguments(typeof(WhisperCppSourceBuildService), "RemoveOutcome", typeof(WhisperCppSourceBuildRemoveOutcome))]
    [Arguments(typeof(StableDiffusionCppSourceBuildService), "RemoveOutcome", typeof(StableDiffusionCppSourceBuildRemoveOutcome))]
    public void PublicContractEnum_MatchesTheSharedBaseEnum_NameForNameAndValueForValue(Type service, string baseEnumName, Type contractEnum)
    {
        // The base is generic, so its nested enum is too: close it with the runtime's own type arguments.
        var closedBase = service.BaseType!;
        var openEnum = closedBase.GetGenericTypeDefinition().GetNestedType(baseEnumName, BindingFlags.NonPublic);
        AssertEx.NotNull(openEnum, $"The source-build base declares no nested enum {baseEnumName}.");
        var baseEnum = openEnum!.MakeGenericType(closedBase.GetGenericArguments());

        AssertEx.Equal(Describe(baseEnum), Describe(contractEnum));
    }

    private static string Describe(Type enumType) =>
        string.Join(", ", Enum.GetNames(enumType).Select(name => $"{name}={Convert.ToInt64(Enum.Parse(enumType, name), CultureInfo.InvariantCulture)}"));
}
