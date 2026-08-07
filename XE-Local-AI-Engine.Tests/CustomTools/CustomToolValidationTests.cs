namespace XE_Local_AI_Engine.Tests.CustomTools;

using XE_Local_AI_Engine.Client.Services.CustomTools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Shared validators: interpreter/shell + script-extension denylist (C1/M4), absolute-path, and MAF-safe name.</summary>
public sealed class CustomToolValidationTests
{
    [Test]
    [Arguments("/bin/bash")]
    [Arguments("/usr/bin/sh")]
    [Arguments("/usr/bin/python3")]
    [Arguments("/usr/bin/python3.12")]
    [Arguments("/usr/bin/node")]
    [Arguments("/usr/bin/perl")]
    [Arguments("/usr/bin/env")]
    [Arguments("/usr/bin/sudo")]
    [Arguments("/usr/bin/ssh")]
    [Arguments("/usr/bin/xargs")]
    [Arguments("/usr/bin/awk")]
    [Arguments("C:\\Windows\\System32\\cmd.exe")]
    [Arguments("C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    [Arguments("C:\\tools\\run.bat")]
    [Arguments("C:\\tools\\run.cmd")]
    [Arguments("C:\\tools\\run.ps1")]
    [Arguments("C:\\tools\\run.vbs")]
    public async Task IsInterpreterOrShell_ForDeniedExecutable_ReturnsTrue(string path)
    {
        AssertEx.True(CustomToolValidation.IsInterpreterOrShell(path), $"Expected {path} to be rejected.");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("/usr/bin/git")]
    [Arguments("/usr/local/bin/mytool")]
    [Arguments("C:\\tools\\mytool.exe")]
    public async Task IsInterpreterOrShell_ForRegularExecutable_ReturnsFalse(string path)
    {
        AssertEx.False(CustomToolValidation.IsInterpreterOrShell(path), $"Expected {path} to be allowed.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAbsolutePath_DistinguishesAbsoluteFromRelative()
    {
        AssertEx.True(CustomToolValidation.IsAbsolutePath(OperatingSystem.IsWindows() ? "C:\\tools\\git.exe" : "/usr/bin/git"));
        AssertEx.False(CustomToolValidation.IsAbsolutePath("git"));
        AssertEx.False(CustomToolValidation.IsAbsolutePath("./git"));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("custom__weather", true)]
    [Arguments("custom__weather_lookup_v2", true)]
    [Arguments("weather", false)] // missing prefix
    [Arguments("custom__Weather", false)] // uppercase
    [Arguments("custom__", false)] // empty slug
    [Arguments("custom__weather-lookup", false)] // hyphen not allowed
    public async Task IsValidToolName_EnforcesMafSafeShape(string name, bool expected)
    {
        AssertEx.Equal(expected, CustomToolValidation.IsValidToolName(name));
        await Task.CompletedTask;
    }
}
