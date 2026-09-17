namespace XE_Local_AI_Engine.Tests.Containers;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Startup validation of the application-container runtime configuration.
///     <para>
///         Two of these values are load-bearing beyond their own calls: the client's HTTP request timeout is sized
///         from the probe timeout and the stop grace period, so a nonsense value there does not produce a nonsense
///         setting — it produces a half-hour image pull cut off by a transport timeout, or a graceful stop killed
///         before the application has written its state. That is why they fail at start rather than at first use.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerRuntimeOptionsValidatorTests
{
    [Test]
    public void ValidOptions_Pass()
    {
        AssertEx.True(Validate(new ContainerRuntimeOptions()).Succeeded,
            "The shipped defaults must validate; a default configuration that cannot start is not a default.");
    }

    [Test]
    [Arguments("1.41")]
    [Arguments("0.0")]
    [Arguments("1.99")]
    public void AMajorMinorApiVersion_IsAccepted(string version)
    {
        AssertEx.True(Validate(new ContainerRuntimeOptions
        {
            MinimumApiVersion = version
        }).Succeeded);
    }

    [Test]
    [Arguments("1")]
    [Arguments("1.41.0")]
    [Arguments("v1.41")]
    [Arguments("-1.41")]
    [Arguments("1.-41")]
    [Arguments("")]
    [Arguments("   ")]
    public void MinimumApiVersion_ThatIsNotMajorMinor_Fails(string version)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                MinimumApiVersion = version
            },
            nameof(ContainerRuntimeOptions.MinimumApiVersion));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(121)]
    public void ProbeTimeout_OutOfRange_Fails(int seconds)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                DaemonProbeTimeoutSeconds = seconds
            },
            nameof(ContainerRuntimeOptions.DaemonProbeTimeoutSeconds));
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(241)]
    public void PullTimeout_OutOfRange_Fails(int minutes)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                PullTimeoutMinutes = minutes
            },
            nameof(ContainerRuntimeOptions.PullTimeoutMinutes));
    }

    [Test]
    [Arguments(-1)]
    [Arguments(601)]
    public void StopGrace_OutOfRange_Fails(int seconds)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                StopGracePeriodSeconds = seconds
            },
            nameof(ContainerRuntimeOptions.StopGracePeriodSeconds));
    }

    [Test]
    [Arguments(-1)]
    [Arguments(601)]
    public void ResolutionCache_OutOfRange_Fails(int seconds)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                ResolutionCacheSeconds = seconds
            },
            nameof(ContainerRuntimeOptions.ResolutionCacheSeconds));
    }

    [Test]
    public void AZeroResolutionCache_IsAcceptedBecauseDisablingTheCacheIsAChoice()
    {
        // The lower bound of the two second-valued windows is 0, not 1: "probe every time" and "kill immediately" are
        // both things an operator may legitimately want, and refusing them would be this validator inventing policy.
        AssertEx.True(Validate(new ContainerRuntimeOptions
        {
            ResolutionCacheSeconds = 0,
            StopGracePeriodSeconds = 0
        }).Succeeded);
    }

    [Test]
    [Arguments("unix:///var/run/docker.sock")]
    [Arguments("npipe://./pipe/docker_engine")]
    [Arguments("/var/run/docker.sock")]
    [Arguments("")]
    [Arguments(null)]
    public void ADaemonEndpointThatIsAnAbsoluteUriOrPathOrBlank_IsAccepted(string? endpoint)
    {
        // Blank means "discover one", which is the shipped default. A remote transport is NOT refused here: that
        // refusal has to cover a DOCKER_HOST this setting never sees, so it lives in the resolver.
        AssertEx.True(Validate(new ContainerRuntimeOptions
        {
            DaemonEndpoint = endpoint
        }).Succeeded);
    }

    [Test]
    [Arguments("docker.sock")]
    [Arguments("../docker.sock")]
    public void DaemonEndpoint_ThatIsNeitherUriNorAbsolutePath_Fails(string endpoint)
    {
        AssertFails(new ContainerRuntimeOptions
            {
                DaemonEndpoint = endpoint
            },
            nameof(ContainerRuntimeOptions.DaemonEndpoint));
    }

    [Test]
    public void EveryFailure_NamesItsFullConfigurationKey()
    {
        // One options record with every value wrong, so the assertion covers the whole failure set rather than
        // whichever one a single-fault case happens to reach first.
        var result = Validate(new ContainerRuntimeOptions
        {
            MinimumApiVersion = "nonsense",
            DaemonProbeTimeoutSeconds = 0,
            PullTimeoutMinutes = 0,
            StopGracePeriodSeconds = -1,
            ResolutionCacheSeconds = -1,
            DaemonEndpoint = "docker.sock"
        });

        AssertEx.True(result.Failed);
        var failures = result.Failures?.ToArray() ?? [];
        AssertEx.Equal(expected: 6, failures.Length, string.Join(" | ", failures));

        foreach (var failure in failures)
        {
            AssertEx.Contains(failure,
                ContainerRuntimeOptions.SectionName + ":",
                StringComparison.Ordinal,
                "An operator reading a start-up failure is looking for the line to edit, so every message names the "
                + $"full configuration key and not just the property: {failure}");
        }
    }

    private static void AssertFails(ContainerRuntimeOptions options, string property)
    {
        var result = Validate(options);

        AssertEx.True(result.Failed, $"'{property}' was expected to fail validation.");
        AssertEx.Contains(string.Join(" | ", result.Failures ?? []),
            ContainerRuntimeOptions.SectionName + ":" + property,
            StringComparison.Ordinal);
    }

    private static ValidateOptionsResult Validate(ContainerRuntimeOptions options)
    {
        return new ContainerRuntimeOptionsValidator().Validate(name: null, options);
    }
}
