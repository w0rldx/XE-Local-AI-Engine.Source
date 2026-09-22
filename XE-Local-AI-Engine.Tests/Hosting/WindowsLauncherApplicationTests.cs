namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.WindowsLauncher;

[Category(TestCategories.Unit)]
public sealed class WindowsLauncherApplicationTests
{
    [Test]
    [Arguments("--browser")]
    [Arguments("--headless")]
    [Arguments("--no-browser")]
    [Arguments("--mcp-only")]
    [Arguments("--help")]
    [Arguments("--status")]
    [Arguments("--setup")]
    [Arguments("--mcp-key=agentic")]
    [Arguments("--reset-admin-password=secret")]
    [Arguments("--knowledge-downgrade-preflight")]
    [Arguments("--knowledge-downgrade-export")]
    public void SelectManagedEntryPoint_OperatorAndBrowserModesBypassShell(string argument) =>
        AssertEx.Equal("XE-Local-AI-Engine.Client.dll", WindowsLauncherApplication.SelectManagedEntryPoint([argument]));

    [Test]
    public void SelectManagedEntryPoint_DefaultAndUpdateRestartOpenShell()
    {
        AssertEx.Equal("XE-Local-AI-Engine.Desktop.dll", WindowsLauncherApplication.SelectManagedEntryPoint([]));
        AssertEx.Equal("XE-Local-AI-Engine.Desktop.dll", WindowsLauncherApplication.SelectManagedEntryPoint(["--desktop", "--port", "41234"]));
    }

    [Test]
    [Arguments("--browser")]
    [Arguments("--headless")]
    [Arguments("--no-browser")]
    [Arguments("--mcp-only")]
    [Arguments("--help")]
    [Arguments("--status")]
    [Arguments("--setup")]
    [Arguments("--mcp-key=agentic")]
    [Arguments("--reset-admin-password=secret")]
    [Arguments("--knowledge-downgrade-preflight")]
    [Arguments("--knowledge-downgrade-export")]
    public void ShouldDetachConsole_KeepsTheConsoleForEveryModeThatPrintsToIt(string argument) =>
        AssertEx.False(WindowsLauncherApplication.ShouldDetachConsole([argument]),
            $"{argument} writes to the inherited console, so the launcher must keep it.");

    [Test]
    public void ShouldDetachConsole_ReleasesTheConsoleOnlyForTheDesktopShell()
    {
        AssertEx.True(WindowsLauncherApplication.ShouldDetachConsole([]), "A plain double-click starts the GUI shell.");
        AssertEx.True(WindowsLauncherApplication.ShouldDetachConsole(["--desktop", "--port", "41234"]),
            "An update restart also starts the GUI shell.");
        AssertEx.False(WindowsLauncherApplication.ShouldDetachConsole(["--desktop", "--status"]),
            "A mode argument that prints wins over the desktop argument.");
    }

    [Test]
    [Arguments("10.0.11", true)]
    [Arguments("10.0.12", true)]
    [Arguments("10.1.0", false)]
    [Arguments("11.0.0", false)]
    [Arguments("10.0.10", false)]
    public void HasCompatibleAspNetCoreRuntime_RequiresThePinnedMajorMinorAndServicingFloor(string installed,
        bool expected)
    {
        var inventory = $"Microsoft.NETCore.App 10.0.11 [C:\\dotnet\\shared]{Environment.NewLine}"
                        + $"Microsoft.AspNetCore.App {installed} [C:\\dotnet\\shared]{Environment.NewLine}";

        AssertEx.Equal(expected, WindowsLauncherApplication.HasCompatibleAspNetCoreRuntime(inventory, new Version(10, 0, 11)));
    }

    [Test]
    public void ResolveRequiredAspNetCoreRuntime_ReadsTheManagedApplicationRuntimeConfig()
    {
        const string runtimeConfig = """
                                     {
                                       "runtimeOptions": {
                                         "frameworks": [
                                           { "name": "Microsoft.NETCore.App", "version": "10.0.11" },
                                           { "name": "Microsoft.AspNetCore.App", "version": "10.0.11" }
                                         ]
                                       }
                                     }
                                     """;

        AssertEx.Equal(new Version(10, 0, 11), WindowsLauncherApplication.ResolveRequiredAspNetCoreRuntime(runtimeConfig));
    }

    [Test]
    public async Task ResolveRequiredAspNetCoreRuntime_MissingFrameworksArrayFailsWithAnActionableError()
    {
        var exception = await AssertEx.ThrowsAsync<InvalidDataException>(() => Task.FromResult(WindowsLauncherApplication.ResolveRequiredAspNetCoreRuntime("{ \"runtimeOptions\": {} }")));

        AssertEx.Contains(exception.Message, "frameworks array");
    }

    [Test]
    public void MissingPayloadFiles_RequiresTheManagedEntrypointAndRuntimeMetadata()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XE-Local-AI-Engine.Client.dll",
            "XE-Local-AI-Engine.Desktop.dll",
            "XE-Local-AI-Engine.Desktop.deps.json",
            "XE-Local-AI-Engine.Desktop.runtimeconfig.json",
            "XE-Local-AI-Engine.Client.deps.json",
            "XE-Local-AI-Engine.Client.runtimeconfig.json",
            "appsettings.AppUpdate.json",
            "wwwroot/index.html",
            "LICENSE",
            "NOTICE"
        };

        AssertEx.Empty(WindowsLauncherApplication.MissingPayloadFiles(present.Contains));
        present.Remove("wwwroot/index.html");
        AssertEx.Equal("wwwroot/index.html", WindowsLauncherApplication.MissingPayloadFiles(present.Contains).Single());
    }

    [Test]
    public void CreateManagedArguments_PreservesEveryVelopackArgument()
    {
        var arguments = WindowsLauncherApplication.CreateManagedArguments("C:\\portable current\\XE-Local-AI-Engine.Client.dll",
            ["--veloapp-obsolete", "1.2.3", "value with spaces"]);

        AssertEx.Equal(expected: 4, arguments.Count);
        AssertEx.Equal("C:\\portable current\\XE-Local-AI-Engine.Client.dll", arguments[0]);
        AssertEx.Equal("--veloapp-obsolete", arguments[1]);
        AssertEx.Equal("1.2.3", arguments[2]);
        AssertEx.Equal("value with spaces", arguments[3]);
    }

    [Test]
    public void CreateManagedArguments_PreservesAgenticLaunchArguments()
    {
        var arguments = WindowsLauncherApplication.CreateManagedArguments("app.dll", ["--mcp-only", "--port", "41234"]);

        AssertEx.True(arguments.SequenceEqual(["app.dll", "--mcp-only", "--port", "41234"]),
            "The launcher must preserve every agentic launch token in order.");
    }
}
