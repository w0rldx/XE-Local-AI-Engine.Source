namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>The boot id a process receipt is bound to: a read failure is "unknown", never an id that could match or differ.</summary>
[Category(TestCategories.Unit)]
public sealed class LinuxBootIdTests
{
    [Test]
    public async Task Read_TrimsTheFile()
    {
        using var temp = new TempDirectory("xe-boot-id");
        var path = temp.FilePath("boot_id");
        await File.WriteAllTextAsync(path, "0f3c1a2b-4d5e-6f70-8192-a3b4c5d6e7f8\n");

        AssertEx.Equal("0f3c1a2b-4d5e-6f70-8192-a3b4c5d6e7f8", LinuxBootId.Read(path));
    }

    [Test]
    [Arguments("missing")]
    [Arguments("blank")]
    [Arguments("directory")]
    public async Task Read_WhenTheFileIsMissingBlankOrUnreadable_IsNull(string failure)
    {
        using var temp = new TempDirectory("xe-boot-id");
        var path = temp.FilePath("boot_id");
        if (failure == "blank")
        {
            await File.WriteAllTextAsync(path, " \n");
        }
        else if (failure == "directory")
        {
            _ = Directory.CreateDirectory(path);
        }

        AssertEx.Null(LinuxBootId.Read(path));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Current_OnLinux_IsTheKernelsBootId()
    {
        var kernel = (await File.ReadAllTextAsync("/proc/sys/kernel/random/boot_id")).Trim();

        AssertEx.Equal(kernel, LinuxBootId.Current);
    }

    [Test]
    [ExcludeOn(OS.Linux)]
    public void Current_OffLinux_IsNull() =>
        AssertEx.Null(LinuxBootId.Current);
}
