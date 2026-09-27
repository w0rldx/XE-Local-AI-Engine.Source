namespace XE_Local_AI_Engine.Tests.Providers.Python;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The per-RID choices of the shared layer, judged as pure functions of an explicit platform, never the host's.</summary>
[Category(TestCategories.Unit)]
public sealed class ManagedPythonPlatformTests
{
    [Test]
    public void Resolve_LinuxX64_PicksTheGnuTarball()
    {
        var asset = ManagedPythonPins.Resolve(isWindows: false, isLinux: true, Architecture.X64);

        AssertEx.Equal("linux-x64", asset.Rid);
        AssertEx.Equal("uv-x86_64-unknown-linux-gnu.tar.gz", asset.AssetName);
        AssertEx.Equal(Path.Combine("store", "uv", ManagedPythonPins.UvVersion, "uv-x86_64-unknown-linux-gnu", "uv"), asset.ExecutablePath("store"));
    }

    [Test]
    public void Resolve_WindowsX64_PicksTheMsvcZip_WithAFlatLayout()
    {
        var asset = ManagedPythonPins.Resolve(isWindows: true, isLinux: false, Architecture.X64);

        AssertEx.Equal("win-x64", asset.Rid);
        AssertEx.Equal("uv-x86_64-pc-windows-msvc.zip", asset.AssetName);
        AssertEx.Equal(64, asset.Sha256.Length);
        AssertEx.Equal(Path.Combine("store", "uv", ManagedPythonPins.UvVersion, "uv.exe"), asset.ExecutablePath("store"));
        AssertEx.Equal($"https://github.com/astral-sh/uv/releases/download/{ManagedPythonPins.UvVersion}/uv-x86_64-pc-windows-msvc.zip",
            asset.DownloadUri().ToString());
    }

    [Test]
    [Arguments(false, true, Architecture.Arm64)]
    [Arguments(true, false, Architecture.Arm64)]
    [Arguments(true, false, Architecture.X86)]
    [Arguments(false, false, Architecture.X64)]
    public void Resolve_AnUnpinnedPlatform_ThrowsAUserSafeMessage(bool isWindows, bool isLinux, Architecture architecture)
    {
        var exception = AssertEx.Throws<ManagedPythonException>(() => ManagedPythonPins.Resolve(isWindows, isLinux, architecture));

        AssertEx.Contains(exception.Message, "Linux x64 and Windows x64 only");
    }

    [Test]
    public void VenvInterpreterPath_FollowsTheVenvLayoutOfEachOs()
    {
        AssertEx.Equal(Path.Combine("venv", ".venv", "bin", "python"), ManagedPythonToolchain.VenvInterpreterPath(Path.Combine("venv", ".venv"), isWindows: false));
        AssertEx.Equal(Path.Combine("venv", ".venv", "Scripts", "python.exe"),
            ManagedPythonToolchain.VenvInterpreterPath(Path.Combine("venv", ".venv"), isWindows: true));
    }

    [Test]
    public void BuildUvEnvironment_ForWindows_RedirectsProfileAndTemp_CaseInsensitively()
    {
        var windows = ManagedPythonEnvironment.BuildUvEnvironment("home", "tmp", "cache", "pythons", isWindows: true);
        var unix = ManagedPythonEnvironment.BuildUvEnvironment("home", "tmp", "cache", "pythons", isWindows: false);

        AssertEx.Equal("home", windows["UserProfile"], "Windows keys must be case-insensitive");
        AssertEx.Equal("tmp", windows["TEMP"]);
        AssertEx.Equal("tmp", windows["TMP"]);
        AssertEx.Equal("0", windows["UV_PYTHON_INSTALL_REGISTRY"]);
        AssertEx.False(unix.ContainsKey("USERPROFILE") || unix.ContainsKey("TEMP") || unix.ContainsKey("TMP"), "the Unix environment is unchanged");
        AssertEx.False(unix.ContainsKey("home"), "Unix keys stay case-sensitive");
    }

    [Test]
    [Arguments(true, false, 101, true)]
    [Arguments(true, false, 100, false)]
    [Arguments(true, true, 200, false)]
    [Arguments(false, false, 200, false)]
    public void ExceedsWindowsPathBudget_RefusesOnlyALongRootOnWindowsWithoutLongPaths(bool isWindows, bool longPaths, int length, bool refused)
    {
        var root = Path.GetFullPath(Path.DirectorySeparatorChar.ToString());
        root += new string('a', length - root.Length);

        AssertEx.Equal(refused, ManagedPythonToolchain.ExceedsWindowsPathBudget(root, isWindows, longPaths));
    }

    [Test]
    [RunOn(OS.Linux)]
    public void ForCurrentPlatform_OnLinux_IsTheSetsidRunner()
    {
        AssertEx.True(PythonToolRunner.ForCurrentPlatform() is LinuxPythonToolRunner);
    }
}
