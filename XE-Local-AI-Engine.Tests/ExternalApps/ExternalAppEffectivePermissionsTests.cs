namespace XE_Local_AI_Engine.Tests.ExternalApps;

using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     What an application may do, and what counts as asking for more. The grants are kept per service because a
///     union hides the two widenings at the bottom of this file: a capability added to a service whose sibling
///     already had it, and a second service becoming writable.
/// </summary>
public sealed class ExternalAppEffectivePermissionsTests
{
    [Test]
    public void From_KeepsApplicationFlagsAndPerServiceGrants()
    {
        var manifest = Manifest(
            permissions: new ApplicationPermissions(Internet: true, LocalNetwork: true, "readOnly", "optional"),
            services:
            [
                ExternalAppTestManifests.Service("app",
                    ports: [ExternalAppTestManifests.UiPort(7000)],
                    capAdd: ["CHOWN"],
                    extraHosts: ["host-gateway"]),
                ExternalAppTestManifests.Service("db", readOnlyRootFilesystem: true, image: ExternalAppTestManifests.SecondImage)
            ]);

        var permissions = ExternalAppEffectivePermissions.From(manifest);

        AssertEx.True(permissions.Internet, "The application-level flags stay application-level.");
        AssertEx.Equal("readOnly", permissions.HostFiles);
        AssertEx.Equal("optional", permissions.Gpu);
        AssertEx.Equal(expected: 2, permissions.Services.Count);
        AssertEx.Contains(permissions.Services["app"].Capabilities, "CHOWN");
        AssertEx.Contains(permissions.Services["app"].PublishedPorts, "app:7000");
        AssertEx.Contains(permissions.Services["app"].ExtraHosts, "host-gateway");
        AssertEx.True(permissions.Services["app"].WritableRootFilesystem, "A service that did not ask for a read-only root is writable.");
        AssertEx.False(permissions.Services["db"].WritableRootFilesystem, "A read-only root filesystem is not a writable one.");
    }

    [Test]
    public void Diff_WithAnUnchangedManifest_IsEmpty()
    {
        var permissions = ExternalAppEffectivePermissions.From(Manifest(services: [ExternalAppTestManifests.Service("app", capAdd: ["CHOWN"])]));

        AssertEx.Empty(ExternalAppEffectivePermissions.Diff(permissions, permissions));
    }

    [Test]
    public void Diff_WhenInternetIsAdded_EmitsInternet()
    {
        AssertName("internet",
            Manifest(permissions: new ApplicationPermissions(Internet: false, LocalNetwork: false, "none", "none")),
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none")));
    }

    [Test]
    public void Diff_WhenLocalNetworkIsAdded_EmitsLocalNetwork()
    {
        AssertName("localNetwork",
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none")),
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: true, "none", "none")));
    }

    [Test]
    [Arguments("none", "readOnly")]
    [Arguments("none", "readWrite")]
    [Arguments("readOnly", "readWrite")]
    public void Diff_WhenHostFilesWidens_EmitsHostFiles(string installed, string target)
    {
        AssertName("hostFiles",
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, installed, "none")),
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, target, "none")));
    }

    [Test]
    public void Diff_WhenHostFilesNarrows_EmitsNothing()
    {
        var installed = ExternalAppEffectivePermissions.From(Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "readWrite", "none")));
        var target = ExternalAppEffectivePermissions.From(Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "readOnly", "none")));

        AssertEx.Empty(ExternalAppEffectivePermissions.Diff(installed, target));
    }

    [Test]
    public void Diff_WhenGpuWidens_EmitsGpu()
    {
        AssertName("gpu",
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none")),
            Manifest(permissions: new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "optional")));
    }

    [Test]
    public void Diff_WhenACapabilityIsAdded_EmitsCapabilities()
    {
        AssertName("capabilities",
            Manifest(services: [ExternalAppTestManifests.Service("app", capAdd: ["CHOWN"])]),
            Manifest(services: [ExternalAppTestManifests.Service("app", capAdd: ["CHOWN", "SETUID"])]));
    }

    /// <summary>
    ///     The redistribution a union would hide: the application already held <c>CHOWN</c> on one service, so the
    ///     merged set does not change even though a second service just gained it.
    /// </summary>
    [Test]
    public void Diff_WhenACapabilityIsAddedToAServiceThatLackedIt_EmitsCapabilities()
    {
        var installed = Manifest(services:
        [
            ExternalAppTestManifests.Service("app", capAdd: ["CHOWN"]),
            ExternalAppTestManifests.Service("db", image: ExternalAppTestManifests.SecondImage)
        ]);
        var target = Manifest(services:
        [
            ExternalAppTestManifests.Service("app", capAdd: ["CHOWN"]),
            ExternalAppTestManifests.Service("db", capAdd: ["CHOWN"], image: ExternalAppTestManifests.SecondImage)
        ]);

        AssertName("capabilities", installed, target);
    }

    /// <summary>The same blind spot for the root filesystem: one service was already writable, so a union sees nothing.</summary>
    [Test]
    public void Diff_WhenASecondServiceBecomesWritable_EmitsWritableRootFilesystem()
    {
        var installed = Manifest(services:
        [
            ExternalAppTestManifests.Service("app"),
            ExternalAppTestManifests.Service("db", readOnlyRootFilesystem: true, image: ExternalAppTestManifests.SecondImage)
        ]);
        var target = Manifest(services:
        [
            ExternalAppTestManifests.Service("app"),
            ExternalAppTestManifests.Service("db", image: ExternalAppTestManifests.SecondImage)
        ]);

        AssertName("writableRootFilesystem", installed, target);
    }

    [Test]
    public void Diff_WhenAPortIsPublished_EmitsPublishedPorts()
    {
        AssertName("publishedPorts",
            Manifest(services: [ExternalAppTestManifests.Service("app")]),
            Manifest(services: [ExternalAppTestManifests.Service("app", ports: [ExternalAppTestManifests.UiPort(7000)])]));
    }

    [Test]
    public void Diff_WhenAnExtraHostIsAdded_EmitsExtraHosts()
    {
        AssertName("extraHosts",
            Manifest(services: [ExternalAppTestManifests.Service("app")]),
            Manifest(services: [ExternalAppTestManifests.Service("app", extraHosts: ["host-gateway"])]));
    }

    [Test]
    public void Diff_WhenTheTargetAddsAWholeService_ContributesEverythingItDeclares()
    {
        var installed = Manifest(services: [ExternalAppTestManifests.Service("app")]);
        var target = Manifest(services:
        [
            ExternalAppTestManifests.Service("app"),
            ExternalAppTestManifests.Service("db",
                ports: [ExternalAppTestManifests.UiPort(5432)],
                capAdd: ["SETUID"],
                extraHosts: ["host-gateway"],
                image: ExternalAppTestManifests.SecondImage)
        ]);

        var added = ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed), ExternalAppEffectivePermissions.From(target));

        AssertEx.Contains(added, "capabilities");
        AssertEx.Contains(added, "publishedPorts");
        AssertEx.Contains(added, "extraHosts");
        AssertEx.Contains(added, "writableRootFilesystem");
    }

    /// <summary>Losing a grant is not a widening, so a service that has left the target contributes nothing.</summary>
    [Test]
    public void Diff_WhenAServiceLeaves_EmitsNothing()
    {
        var installed = Manifest(services:
        [
            ExternalAppTestManifests.Service("app"),
            ExternalAppTestManifests.Service("db", capAdd: ["SETUID"], image: ExternalAppTestManifests.SecondImage)
        ]);
        var target = Manifest(services: [ExternalAppTestManifests.Service("app")]);

        AssertEx.Empty(ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed), ExternalAppEffectivePermissions.From(target)));
    }

    /// <summary>
    ///     The eight names are a fixed vocabulary shared with the acknowledgement payload and the permission panel;
    ///     a ninth name, or a renamed one, would reach a browser that has no label for it.
    /// </summary>
    [Test]
    public void Vocabulary_IsTheEightAgreedNames()
    {
        AssertEx.Equal("internet,localNetwork,hostFiles,gpu,capabilities,writableRootFilesystem,publishedPorts,extraHosts",
            string.Join(",", ExternalAppEffectivePermissions.Vocabulary));
    }

    private static void AssertName(string expected, ApplicationManifest installed, ApplicationManifest target)
    {
        var added = ExternalAppEffectivePermissions.Diff(ExternalAppEffectivePermissions.From(installed), ExternalAppEffectivePermissions.From(target));

        AssertEx.Contains(added, expected, $"Expected '{expected}', got '{string.Join(",", added)}'.");
        AssertEx.True(added.All(name => ExternalAppEffectivePermissions.Vocabulary.Contains(name)), "Every emitted name is from the fixed vocabulary.");
    }

    private static ApplicationManifest Manifest(IReadOnlyList<ApplicationService>? services = null, ApplicationPermissions? permissions = null)
    {
        return ExternalAppTestManifests.Manifest(services ?? [ExternalAppTestManifests.Service("app")], permissions: permissions);
    }
}
