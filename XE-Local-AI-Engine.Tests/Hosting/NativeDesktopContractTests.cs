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
