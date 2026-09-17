namespace XE_Local_AI_Engine.Tests.Containers;

using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one place the stored, wire and engine representations of the runtime selection meet.
///     <para>
///         Stored and carried as a string on purpose: the node settings file is serialized with web defaults and no
///         enum converter, so an enum would persist as <c>0</c>/<c>1</c> in a file an operator hand-edits and would
///         change meaning silently if a value were ever inserted between them.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerRuntimeSelectionTests
{
    [Test]
    [Arguments("auto", ContainerRuntimeSelection.Auto)]
    [Arguments("AUTO", ContainerRuntimeSelection.Auto)]
    [Arguments("Auto", ContainerRuntimeSelection.Auto)]
    [Arguments("docker", ContainerRuntimeSelection.Docker)]
    [Arguments("Docker", ContainerRuntimeSelection.Docker)]
    [Arguments("DOCKER", ContainerRuntimeSelection.Docker)]
    [Arguments("  docker  ", ContainerRuntimeSelection.Docker)]
    public void TryParse_AcceptsTheAllowListOrdinalIgnoreCase(string value, ContainerRuntimeSelection expected)
    {
        AssertEx.True(ContainerRuntimeSelectionParser.TryParse(value, out var parsed));
        AssertEx.Equal(expected, parsed);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("podman")]
    [Arguments("0")]
    [Arguments("1")]
    public void TryParse_RejectsNullBlankAndUnknownAndStillYieldsAuto(string? value)
    {
        // Both halves matter. The false is what lets an API validator return 400 on a value it does not recognise;
        // the Auto is what stops a caller that ignores the result from acting on an uninitialised selection. A stored
        // "0" is rejected on purpose: numbers are what the enum would have persisted as, and reading them back would
        // re-introduce the silent renumbering the string spelling exists to prevent.
        AssertEx.False(ContainerRuntimeSelectionParser.TryParse(value, out var parsed));
        AssertEx.Equal(ContainerRuntimeSelection.Auto, parsed);
    }

    [Test]
    [Arguments(ContainerRuntimeSelection.Auto, "auto")]
    [Arguments(ContainerRuntimeSelection.Docker, "docker")]
    public void Format_WritesTheStoredSpelling(ContainerRuntimeSelection selection, string expected)
    {
        AssertEx.Equal(expected, ContainerRuntimeSelectionParser.Format(selection));
    }

    [Test]
    public void EveryEnumMember_RoundTripsThroughFormatAndTryParse()
    {
        // Enumerated rather than listed, so a third provider added to the enum without its spelling is red here
        // instead of silently formatting as "auto" and switching every node back to the default.
        foreach (var selection in Enum.GetValues<ContainerRuntimeSelection>())
        {
            var formatted = ContainerRuntimeSelectionParser.Format(selection);

            AssertEx.True(ContainerRuntimeSelectionParser.TryParse(formatted, out var parsed),
                $"'{formatted}' is what Format produced for {selection} and TryParse does not accept it.");
            AssertEx.Equal(selection, parsed);
        }
    }

    [Test]
    public void TheStoredDefaultSpelling_IsAuto()
    {
        AssertEx.Equal("auto", ContainerRuntimeSelectionParser.Auto);
        AssertEx.Equal("docker", ContainerRuntimeSelectionParser.Docker);
        AssertEx.Equal(ContainerRuntimeSelectionParser.Auto, ContainerRuntimeSelectionParser.Format(default));
    }
}
