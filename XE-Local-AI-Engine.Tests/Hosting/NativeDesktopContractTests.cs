namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Desktop.Linux;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.WindowsLauncher;

/// <summary>
///     Pins the literals the desktop shell and the engine must agree on without sharing an assembly.
/// </summary>
/// <remarks>
///     The shell holds no project reference to any engine assembly (ADR 0013), so their protocol — ownership
///     environment variables, launch arguments, data folder name, restricted document policy — is duplicated on both
///     sides and no build breaks when one copy drifts. This test project is the only place that references both;
///     change a literal on both sides in the same commit.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class NativeDesktopContractTests
{
    [Test]
    public void LifetimePipeVariable_MatchesBetweenShellAndEngine() =>
        AssertEx.Equal(DesktopParentLifetime.EnvironmentVariable, DesktopEngineSession.LifetimePipeVariable);

    [Test]
    public void SupervisorProcessIdVariable_MatchesBetweenShellAndEngine() =>
        AssertEx.Equal(FrameworkDependentVelopackBootstrap.SupervisorProcessIdVariable,
            DesktopEngineSession.SupervisorProcessIdVariable);

    [Test]
    public void ApplicationDataFolderName_MatchesBetweenShellAndEngine() =>
        AssertEx.Equal(DesktopBootstrap.ApplicationDataFolderName, DesktopStartupOptions.ApplicationDataFolderName);

    [Test]
    public void DesktopArgument_MatchesBetweenShellAndEngine() =>
        AssertEx.Equal(DesktopLaunch.DesktopArgument, DesktopEngineSession.DesktopArgument);

    [Test]
    public void NoBrowserArgument_MatchesBetweenShellAndEngine() =>
        AssertEx.Equal(DesktopLaunch.NoBrowserArgument, DesktopEngineSession.NoBrowserArgument);

    [Test]
    public void LaunchModeVariable_MatchesBetweenShellAndEngine()
    {
        AssertEx.Equal(DesktopLaunch.LaunchModeEnvironmentVariable, DesktopCommandLine.LaunchModeVariable);
        AssertEx.Equal(DesktopLaunch.McpOnlyModeValue, DesktopCommandLine.McpOnlyModeValue);
    }

    [Test]
    public void DebugArgument_MatchesBetweenLauncherAndShell() =>
        AssertEx.Equal(WindowsLauncherApplication.DebugArgument, DesktopStartupOptions.DebugArgument);

    [Test]
    public void CommandOverlayRoute_IsARouteTheSpaServes()
    {
        // The SPA, not the engine, owns page routes: the generated route tree is the other side of this literal.
        var routeTree = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Client.React", "src", "routeTree.gen.ts"));

        AssertEx.Equal("/command", DesktopCommandWindow.Route);
        AssertEx.Contains(routeTree, $"fullPath: '{DesktopCommandWindow.Route}'");
    }

    [Test]
    public void LogLevelVariable_IsTheEnvironmentFormOfTheKeyTheEngineLevelSwitchReads()
    {
        // The environment-variable configuration provider maps "__" to the ":" key delimiter.
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [DesktopEngineSession.LogLevelVariable.Replace("__", ConfigurationPath.KeyDelimiter, StringComparison.Ordinal)] = "Debug"
                            })
                            .Build();

        AssertEx.True(new NodeLogLevelSwitch(configuration).Verbose);
    }

    [Test]
    public void RestrictedDocumentPolicy_MatchesBetweenShellAndEngine()
    {
        AssertEx.Equal(NativeDesktopDocumentPolicy.UserAgentMarker, GtkDocumentPolicy.UserAgentMarker);
        AssertEx.Equal(NativeDesktopDocumentPolicy.ContentPolicy, GtkDocumentPolicy.ContentSecurityPolicy);
        AssertEx.Equal(NativeDesktopDocumentPolicy.PermissionsPolicy, GtkDocumentPolicy.PermissionsPolicy);
    }

    [Test]
    public void StartupFailureText_MentionsWebView2OnlyForTheWindowFailure()
    {
        AssertEx.False(DesktopText.StartupFailed.Contains("WebView2", StringComparison.Ordinal),
            "An engine that never reached readiness says nothing about the WebView2 runtime.");
        AssertEx.Contains(DesktopText.WindowFailed, "WebView2");
    }

    [Test]
    public void StartupFailureView_OffersTheWebView2DownloadOnlyAfterTheEngineWasReady()
    {
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopApplication.cs"));

        AssertEx.Contains(source, "includeWebViewLink: engineReady && OperatingSystem.IsWindows()");
        AssertEx.False(source.Contains("includeWebViewLink: OperatingSystem.IsWindows()", StringComparison.Ordinal),
            "An engine failure before readiness must not offer the WebView2 download.");
    }

    [Test]
    public void TrayIcon_IsAttachedToTheApplication_SoThePlatformIconExists()
    {
        // Avalonia 12.1.4 creates the platform tray only on attach; an unattached icon left the tray preference unusable.
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopApplication.cs"));

        AssertEx.Contains(source, "_tray = new TrayIcon");
        AssertEx.Contains(source, "TrayIcon.SetIcons(this, new TrayIcons { _tray });");
        AssertEx.Contains(source, "_tray?.Dispose();");
    }

    [Test]
    public void TrayIcon_UsesTheHighContrastAsset_WithTheFaviconAsFallback()
    {
        // The favicon's dark outline vanished on a dark taskbar; the tray takes a white glyph with a dark stroke.
        var project = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "XE-Local-AI-Engine.Desktop.csproj"));
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopApplication.cs"));

        AssertEx.Contains(project, "<EmbeddedResource Include=\"Assets/TrayIcon.png\" LogicalName=\"TrayIcon.png\" />");
        AssertEx.True(File.Exists(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "Assets", "TrayIcon.png")));
        AssertEx.Contains(source, "GetManifestResourceStream(\"TrayIcon.png\")");
        AssertEx.Contains(source, "Icon = trayStream is null ? icon : new WindowIcon(trayStream),");
    }

    [Test]
    public void WindowIcon_UsesTheFilledHighContrastAsset_WithTheFaviconAsFallback()
    {
        // The outline favicon was low-contrast on the title bar and taskbar button; the window takes the filled hexagon.
        var project = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "XE-Local-AI-Engine.Desktop.csproj"));
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopApplication.cs"));

        AssertEx.Contains(project, "<EmbeddedResource Include=\"Assets/WindowIcon.png\" LogicalName=\"WindowIcon.png\" />");
        AssertEx.True(File.Exists(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "Assets", "WindowIcon.png")));
        AssertEx.Contains(source, "GetManifestResourceStream(\"WindowIcon.png\")");
        AssertEx.Contains(source, "_window!.Icon = windowStream is null ? icon : new WindowIcon(windowStream);");
    }

    [Test]
    public void Program_NamesTheProcessForTheWindowsShell_BeforeAnyWindowExists()
    {
        // Without an explicit AppUserModelID the taskbar groups the dotnet-hosted shell as ".NET Host".
        var program = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "Program.cs"));
        var shell = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopWindowsShell.cs"));
        var engine = program.IndexOf("DesktopEngineCommand.RunAsync(args)", StringComparison.Ordinal);
        var guarded = program.IndexOf("if (OperatingSystem.IsWindows())", StringComparison.Ordinal);
        var named = program.IndexOf("DesktopWindowsShell.NameProcess();", StringComparison.Ordinal);

        AssertEx.Equal("XE.LocalAIEngine.Desktop", DesktopWindowsShell.AppUserModelId);
        AssertEx.True(engine >= 0 && guarded > engine, "The windowless engine child returns before the shell names itself.");
        AssertEx.True(named > guarded, "The setter runs inside the Windows guard.");
        AssertEx.True(named < program.IndexOf("AppBuilder.Configure", StringComparison.Ordinal), "The ID is set before Avalonia starts.");
        AssertEx.Contains(shell, "[LibraryImport(\"shell32.dll\"");
        AssertEx.Contains(shell, "SetCurrentProcessExplicitAppUserModelID(string appId)");
        AssertEx.Contains(shell, "[SupportedOSPlatform(\"windows\")]");
    }

    [Test]
    public void CloseDialog_IsTitledByItsPurpose_AndOffersNoMinimizeOrMaximize()
    {
        var source = File.ReadAllText(RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop", "DesktopCloseDialog.cs"));

        AssertEx.Contains(source, "Title = quitting ? DesktopText.Quit : DesktopText.CloseTitle;");
        AssertEx.Contains(source, "Title = DesktopText.Settings;");
        AssertEx.Contains(source, "CanMinimize = false;");
        AssertEx.Contains(source, "CanMaximize = false;");
        AssertEx.Equal(3, new[]
        {
            DesktopText.Settings,
            DesktopText.Quit,
            DesktopText.CloseTitle
        }.Distinct(StringComparer.Ordinal).Count());
        AssertEx.Contains(source, "Text = DesktopText.Current(Label(current)),");
        AssertEx.Contains(source, "IsDefault = isDefault,");
        AssertEx.Contains(source, "if (isDefault)");
        AssertEx.Contains(source, "button.Classes.Add(\"accent\");");
        AssertEx.Equal(1, source.Split("Classes.Add(", StringSplitOptions.None).Length - 1, "Only the default choice carries the accent.");
        AssertEx.Contains(DesktopText.Current("Keep running in the tray"), "Keep running in the tray");
    }

    [Test]
    public void ShellWindows_UseOneProductTitle()
    {
        var desktop = RepositoryPaths.Combine("XE-Local-AI-Engine.Desktop");
        var stray = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
                             .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                             .Where(static path => File.ReadAllText(path).Contains("\"XE AI-Engine\"", StringComparison.Ordinal))
                             .ToList();

        AssertEx.Equal("XE Local AI Engine", DesktopText.Title);
        AssertEx.Empty(stray, "Every shell window and the tray take DesktopText.Title.");
    }
}
