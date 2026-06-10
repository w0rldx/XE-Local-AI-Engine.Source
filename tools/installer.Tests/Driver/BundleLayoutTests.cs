namespace XE_Local_AI_Engine.Installer.Tests.Driver;

using XE_Local_AI_Engine.Installer.Driver.Windows;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BundleLayoutTests
{
    [Test]
    public void ToWslMountPath_TranslatesDriveLetterAndBackslashes()
    {
        var result = BundleLayout.ToWslMountPath(@"C:\rc\payload\images\img.tar.gz");

        AssertEx.Equal("/mnt/c/rc/payload/images/img.tar.gz", result);
    }

    [Test]
    public void ToWslMountPath_LowercasesDriveLetter()
    {
        var result = BundleLayout.ToWslMountPath(@"D:\bundle\x");

        AssertEx.Equal("/mnt/d/bundle/x", result);
    }

    [Test]
    public void ToWslMountPath_WhenPosixAbsolute_ReturnsUnchanged()
    {
        var result = BundleLayout.ToWslMountPath("/mnt/c/already/posix");

        AssertEx.Equal("/mnt/c/already/posix", result);
    }

    [Test]
    public async Task ToWslMountPath_WhenRelativePath_Throws()
    {
        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            BundleLayout.ToWslMountPath("relative/path/no/drive");
            return Task.CompletedTask;
        });
    }
}
