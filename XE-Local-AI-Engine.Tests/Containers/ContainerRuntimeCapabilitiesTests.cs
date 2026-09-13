namespace XE_Local_AI_Engine.Tests.Containers;

using System.Reflection;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The capability vocabulary a catalog manifest's <c>requires[]</c> is written against, and the check that
///     evaluates it. They live in one type so that they cannot disagree; these tests are what keep that true once the
///     catalog validator states the same nine names on its own side.
/// </summary>
public sealed class ContainerRuntimeCapabilitiesTests
{
    [Test]
    public void CapabilityNames_AreTheNineCamelCaseNamesTheManifestUses()
    {
        AssertEx.Equal(expected: 9, ContainerRuntimeCapabilities.Names.Count);
        AssertEx.Equal("containers,networks,bindStorage,loopbackPortPublishing,healthChecks,restartPolicies,logs,imagePull,gpuDevices",
            string.Join(",", ContainerRuntimeCapabilities.Names));
    }

    /// <summary>
    ///     The names and the flags are two statements of one vocabulary. A flag added without its name would be a
    ///     capability no manifest can ask for; a name added without its flag would be a requirement
    ///     <see cref="ContainerRuntimeCapabilities.FindMissing" /> reports as unmet on a runtime that offers it.
    /// </summary>
    [Test]
    public void EveryName_HasABooleanMemberAndEveryBooleanMemberHasAName()
    {
        var members = typeof(ContainerRuntimeCapabilities)
                      .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                      .Where(static property => property.PropertyType == typeof(bool))
                      .Select(static property => property.Name)
                      .OrderBy(static name => name, StringComparer.Ordinal)
                      .ToArray();

        var expected = ContainerRuntimeCapabilities.Names
                                                   .Select(static name => char.ToUpperInvariant(name[0]) + name[1..])
                                                   .OrderBy(static name => name, StringComparer.Ordinal)
                                                   .ToArray();

        AssertEx.Equal(string.Join(",", expected), string.Join(",", members));
    }

    [Test]
    public void DockerReady_AdvertisesEveryCapabilityExceptGpuDevices()
    {
        var ready = ContainerRuntimeCapabilities.DockerReady;

        AssertEx.Empty(ready.FindMissing(ContainerRuntimeCapabilities.Names.Where(static name => name != "gpuDevices")),
            "A ready Docker daemon offers everything this vocabulary names except GPU devices.");
        AssertEx.False(ready.GpuDevices, "ADR 0010 makes GPU device requests a non-goal; the flag exists to refuse them by name.");
    }

    [Test]
    public void None_OffersNothing()
    {
        AssertEx.Equal(ContainerRuntimeCapabilities.Names.Count,
            ContainerRuntimeCapabilities.None.FindMissing(ContainerRuntimeCapabilities.Names).Count);
    }

    [Test]
    public void FindMissing_NamesEveryUnmetOrUnknownCapability()
    {
        var partial = ContainerRuntimeCapabilities.None with
        {
            Containers = true,
            Networks = true
        };

        var missing = partial.FindMissing(["containers", "networks", "bindStorage", "imagePull"]);

        AssertEx.Equal("bindStorage,imagePull", string.Join(",", missing));
    }

    [Test]
    [Arguments("teleportation")]
    [Arguments("Containers")]
    [Arguments("gpu-devices")]
    [Arguments("")]
    public void AnUnknownName_CountsAsMissingRatherThanAsSatisfied(string unknown)
    {
        // A runtime cannot honour a requirement it does not understand, and reading the unknown as met is how a
        // manifest written against a later schema installs silently and fails at run time. The comparison is ordinal,
        // so a differently-cased spelling is a different name.
        AssertEx.Equal("|" + unknown, "|" + string.Join(",", ContainerRuntimeCapabilities.DockerReady.FindMissing([unknown])));
    }

    [Test]
    public void FindMissing_PreservesTheOrderItWasAskedIn()
    {
        var missing = ContainerRuntimeCapabilities.None.FindMissing(["logs", "containers", "logs"]);

        // Order and duplicates are kept rather than normalised: the result is reported to a user beside the manifest
        // they wrote, so it reads back in the order they wrote it.
        AssertEx.Equal("logs,containers,logs", string.Join(",", missing));
    }

    [Test]
    public void FindMissing_OnANullRequirementList_Throws()
    {
        AssertEx.Throws<ArgumentNullException>(() => ContainerRuntimeCapabilities.DockerReady.FindMissing(required: null!));
    }
}
