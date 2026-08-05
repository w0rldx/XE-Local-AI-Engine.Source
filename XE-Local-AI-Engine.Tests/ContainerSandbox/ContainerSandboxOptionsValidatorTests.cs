namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Startup validation of the container-sandbox configuration.
///     <para>
///         The interesting case is what this validator deliberately does NOT decide. Whether UID 0 means host root
///         depends on the daemon, and this runs with no daemon in reach — so rejecting it here would refuse a correct
///         rootless configuration on one machine while catching nothing extra on the other. The enforcement lives at
///         create time, where the daemon has been probed; these cases pin that split so a later reader does not
///         "restore" the startup rejection and break every rootless install.
///     </para>
/// </summary>
public sealed class ContainerSandboxOptionsValidatorTests
{
    [Test]
    public void Validate_ADigestPinnedImageWithTheEngineIdentityLeftUnset_IsAccepted()
    {
        AssertEx.True(Validate(Options() with
        {
            UserId = null,
            GroupId = null
        }).Succeeded);
    }

    [Test]
    public void Validate_UidAndGidZero_AreAcceptedHereBecauseOnlyTheDaemonCanSettleWhatTheyMean()
    {
        // Not an oversight and not a relaxation of the hardening contract: under a rootless daemon 0 is the invoking user's own
        // unprivileged host account and is the ONLY id that can use an engine-generated bind mount. Against a rootful
        // daemon the same value is refused — by DockerSandboxRuntimeProvider.ResolveIdentity, which has probed the
        // daemon and can tell the two apart.
        AssertEx.True(Validate(Options() with
        {
            UserId = 0,
            GroupId = 0
        }).Succeeded);
    }

    [Test]
    public void Validate_AZeroPairedWithASubordinateId_IsRejectedWithoutNeedingADaemon()
    {
        // This one IS answerable at startup: the two halves of the identity would live in different host accounts
        // under either daemon mode, so the container would not own the group of what it creates.
        var result = Validate(Options() with
        {
            UserId = 0,
            GroupId = 1000
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "must both be 0");
    }

    [Test]
    public void Validate_AMutableImageTag_IsRejected()
    {
        var result = Validate(Options() with
        {
            Image = "alpine:3.22"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "digest-pinned");
    }

    [Test]
    public void Validate_ARelativeMountTarget_IsRejected()
    {
        var result = Validate(Options() with
        {
            WorkspaceMountTarget = "workspace"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "absolute in-container path");
    }

    [Test]
    public void Validate_AScratchAreaThatShadowsTheWorkspace_IsRejected()
    {
        var result = Validate(Options() with
        {
            ScratchMountTarget = "/workspace/scratch"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "must not overlap");
    }

    [Test]
    public void Validate_ATemporaryMountThatShadowsTheWorkspace_IsRejected()
    {
        // The overlap sweep is N-way precisely so a third target cannot be added without being compared to the other
        // two. A tmpfs at an ancestor of the workspace would hide the repository the container was created to build,
        // and the daemon's read-back would still agree, because that is exactly what it was asked for.
        var result = Validate(Options() with
        {
            TempMountTarget = "/workspace/tmp"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "must not overlap");
    }

    [Test]
    public void Validate_ARelativeTemporaryMountTarget_IsRejected()
    {
        var result = Validate(Options() with
        {
            TempMountTarget = "tmp"
        });

        AssertEx.True(result.Failed);
        AssertEx.Contains(string.Join(" ", result.Failures ?? []), "absolute in-container path");
    }

    private static ValidateOptionsResult Validate(ContainerSandboxOptions options)
    {
        return new ContainerSandboxOptionsValidator().Validate(name: null, options);
    }

    private static ContainerSandboxOptions Options()
    {
        return DockerSandboxHardeningTests.Options();
    }
}
