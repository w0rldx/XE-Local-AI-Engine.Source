namespace XE_Local_AI_Engine.Tests.Hosting;

using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Desktop;
using XE_Local_AI_Engine.Desktop.Linux;
using XE_Local_AI_Engine.Tests.Testing;

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
    public void RestrictedDocumentPolicy_MatchesBetweenShellAndEngine()
    {
        AssertEx.Equal(NativeDesktopDocumentPolicy.UserAgentMarker, GtkDocumentPolicy.UserAgentMarker);
        AssertEx.Equal(NativeDesktopDocumentPolicy.ContentPolicy, GtkDocumentPolicy.ContentSecurityPolicy);
        AssertEx.Equal(NativeDesktopDocumentPolicy.PermissionsPolicy, GtkDocumentPolicy.PermissionsPolicy);
    }
}
