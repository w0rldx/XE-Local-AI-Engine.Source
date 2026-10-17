namespace XE_Local_AI_Engine.Cli.Tests;

using System.CommandLine;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class CliRootTests
{
    [Test]
    public async Task Version_PrintsTheInformationalVersion_AndExitsZero()
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var exitCode = await CliRoot.Create().Parse(["version"]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });

        AssertEx.Equal(0, exitCode);
        AssertEx.Equal(CliRoot.InformationalVersion, output.ToString().Trim());
        AssertEx.NotEqual("unknown", CliRoot.InformationalVersion);
        AssertEx.Equal(string.Empty, error.ToString());
    }

    [Test]
    public async Task AnUnknownCommand_FailsWithANonZeroExitCode()
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();

        var exitCode = await CliRoot.Create().Parse(["no-such-command"]).InvokeAsync(new InvocationConfiguration { Output = output, Error = error });

        AssertEx.NotEqual(0, exitCode);
        AssertEx.Contains(error.ToString(), "no-such-command");
    }
}
