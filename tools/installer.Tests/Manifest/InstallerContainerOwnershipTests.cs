namespace XE_Local_AI_Engine.Installer.Tests.Manifest;

using XE_Local_AI_Engine.Installer.Manifest;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InstallerContainerOwnershipTests
{
    [Test]
    public void Ownership_WhenContainerNotInManifest_NotOwned()
    {
        var manifest = new InstallerManifest { ContainerNames = ["ollama", "xe-node-web-server"] };

        AssertEx.False(
            InstallerContainerOwnership.Owns(manifest, "some-foreign-container"),
            "a container not declared in the manifest must not be owned.");
    }

    [Test]
    public void Ownership_WhenContainerInManifest_Owned()
    {
        var manifest = new InstallerManifest { ContainerNames = ["ollama", "xe-node-web-server"] };

        AssertEx.True(
            InstallerContainerOwnership.Owns(manifest, "xe-node-web-server"),
            "a declared container must be owned.");
    }

    [Test]
    public void Ownership_WhenManifestNull_FailsClosed()
    {
        AssertEx.False(
            InstallerContainerOwnership.Owns(null, "ollama"),
            "a null manifest owns nothing (fail-closed).");
    }

    [Test]
    public void Ownership_WhenNameCaseDiffers_NotOwned()
    {
        var manifest = new InstallerManifest { ContainerNames = ["ollama"] };

        AssertEx.False(
            InstallerContainerOwnership.Owns(manifest, "OLLAMA"),
            "ownership is an ordinal (case-sensitive) match, matching ContainerOwnership.Owns.");
    }
}
