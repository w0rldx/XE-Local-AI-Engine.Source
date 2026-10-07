namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="WindowsCommandLine.Join" /> must produce a line that <c>CommandLineToArgvW</c> parses back to the same argv. The
///     expected strings are the CommandLineToArgvW rules worked by hand: a wrong escape silently changes what the contained child runs.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WindowsCommandLineTests
{
    [Test]
    [Arguments("plain", "plain")]
    [Arguments("", "\"\"")]
    [Arguments("two words", "\"two words\"")]
    [Arguments("tab\there", "\"tab\there\"")]
    [Arguments("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [Arguments("a\"b", "\"a\\\"b\"")]
    [Arguments(@"C:\dir\file", @"C:\dir\file")]
    [Arguments(@"\\server\share\", @"\\server\share\")]
    [Arguments(@"C:\with space\", "\"C:\\with space\\\\\"")]
    [Arguments(@"a\""b", "\"a\\\\\\\"b\"")]
    [Arguments(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    public async Task Join_QuotesEachArgument_ByCommandLineToArgvWRules(string argument, string expected)
    {
        AssertEx.Equal("prog.exe " + expected, WindowsCommandLine.Join("prog.exe", [argument]));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Join_WhenProgramPathHasSpaces_QuotesItWithoutEscapingBackslashes()
    {
        var line = WindowsCommandLine.Join(@"C:\Program Files\tool\run.exe", ["x", ""]);

        AssertEx.Equal("\"C:\\Program Files\\tool\\run.exe\" x \"\"", line);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("bad\"prog.exe")]
    [Arguments("nul\0prog.exe")]
    [Arguments("")]
    public async Task Join_WhenProgramPathCannotBeRepresented_Throws(string executable)
    {
        AssertEx.Throws<ArgumentException>(() => WindowsCommandLine.Join(executable, []));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Join_WhenAnArgumentIsNullOrHasNul_Throws()
    {
        AssertEx.Throws<ArgumentException>(() => WindowsCommandLine.Join("prog.exe", ["ok", "nul\0byte"]));
        AssertEx.Throws<ArgumentException>(() => WindowsCommandLine.Join("prog.exe", [null!]));
        await Task.CompletedTask;
    }
}
