namespace XE_Local_AI_Engine.Installer.Tests.Fakes;

using XE_Local_AI_Engine.Installer.Driver.Windows;

/// <summary>Records WriteConfig calls; the real layout logic is covered by WindowsHostConfigWriter tests.</summary>
internal sealed class FakeHostConfigWriter : IInstallerHostConfigWriter
{
    public int WriteCount { get; private set; }

    public Task WriteAsync(string bundlePath, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        return Task.CompletedTask;
    }
}
