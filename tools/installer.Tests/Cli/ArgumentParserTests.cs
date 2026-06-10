namespace XE_Local_AI_Engine.Installer.Tests.Cli;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ArgumentParserTests
{
    [Test]
    public void ArgParse_WhenInstallNoBundle_ReturnsUsageError()
    {
        var result = ArgumentParser.Parse(["install"]);

        AssertEx.False(result.IsSuccess, "install without --bundle must fail.");
        AssertEx.NotNullOrEmpty(result.ErrorMessage);
        AssertEx.Contains(result.ErrorMessage, "--bundle");
        AssertEx.Contains(result.Usage, "Usage: xe-installer");
    }

    [Test]
    public void ArgParse_WhenUnknownVerb_ReturnsError()
    {
        var result = ArgumentParser.Parse(["frobnicate"]);

        AssertEx.False(result.IsSuccess, "an unknown verb must be rejected.");
        AssertEx.Contains(result.ErrorMessage, "Unknown verb");
    }

    [Test]
    public void ArgParse_WhenInstallWithBundle_Succeeds()
    {
        var result = ArgumentParser.Parse(["install", "--bundle", "/tmp/bundle"]);

        AssertEx.True(result.IsSuccess, "install with --bundle must parse.");
        var arguments = AssertEx.NotNull(result.Arguments);
        AssertEx.Equal(InstallerVerb.Install, arguments.Verb);
        AssertEx.Equal("/tmp/bundle", arguments.BundlePath);
    }

    [Test]
    public void ArgParse_WhenBundleUsesInlineValue_Succeeds()
    {
        var result = ArgumentParser.Parse(["install", "--bundle=/tmp/b"]);

        AssertEx.True(result.IsSuccess, "--bundle=value form must parse.");
        AssertEx.Equal("/tmp/b", AssertEx.NotNull(result.Arguments).BundlePath);
    }

    [Test]
    public void ArgParse_WhenBundleValueMissing_ReturnsError()
    {
        var result = ArgumentParser.Parse(["install", "--bundle", "--yes"]);

        AssertEx.False(result.IsSuccess, "a bundle flag followed by another flag is not a value.");
        AssertEx.Contains(result.ErrorMessage, "requires a value");
    }

    [Test]
    public void ArgParse_WhenRemoveWithYesAndKeepModels_ParsesFlags()
    {
        var result = ArgumentParser.Parse(["remove", "--yes", "--keep-models", "--dry-run"]);

        AssertEx.True(result.IsSuccess, "remove flags must parse.");
        var arguments = AssertEx.NotNull(result.Arguments);
        AssertEx.Equal(InstallerVerb.Remove, arguments.Verb);
        AssertEx.True(arguments.AssumeYes, "--yes must set AssumeYes.");
        AssertEx.True(arguments.KeepModels, "--keep-models must set KeepModels.");
        AssertEx.True(arguments.DryRun, "--dry-run must set DryRun.");
    }

    [Test]
    public void ArgParse_WhenNoArgs_ReturnsUsageError()
    {
        var result = ArgumentParser.Parse([]);

        AssertEx.False(result.IsSuccess, "empty args must fail.");
        AssertEx.Contains(result.ErrorMessage, "No verb");
    }
}
