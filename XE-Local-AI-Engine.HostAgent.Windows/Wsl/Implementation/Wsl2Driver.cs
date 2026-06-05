namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl.Implementation;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

public sealed class Wsl2Driver
{
    private readonly HostAgentWslOptions _options;
    private readonly IWindowsProcessRunner _processRunner;

    public Wsl2Driver(IWindowsProcessRunner processRunner, IOptions<HostAgentWslOptions> options)
    {
        _processRunner = processRunner;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(WslCommandAllowlist.ListRunningQuiet(), cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public Task<WslCommandResult> ProbeStatusAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.Status(), cancellationToken);
    }

    public Task<WslCommandResult> ListVerboseAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.ListVerbose(), cancellationToken);
    }

    public Task<WslCommandResult> InstallNoDistributionAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.InstallNoDistribution(), cancellationToken);
    }

    public Task<WslCommandResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.InstallPath);
        return RunAsync(WslCommandAllowlist.Import(_options.DistroName, _options.InstallPath, _options.RootfsTarballPath), cancellationToken);
    }

    public Task<WslCommandResult> UnregisterAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.Unregister(_options.DistroName), cancellationToken);
    }

    public Task<WslCommandResult> TerminateAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.Terminate(_options.DistroName), cancellationToken);
    }

    public Task<WslCommandResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.Shutdown(), cancellationToken);
    }

    public Task<WslCommandResult> BootstrapAsync(string script, string expectedSha256, CancellationToken cancellationToken = default)
    {
        VerifyScriptHash(script, expectedSha256);
        return RunAsync(WslCommandAllowlist.BootstrapScript(_options.DistroName, script, _options.ScriptCommandTimeout), cancellationToken);
    }

    public Task<WslCommandResult> RuntimeInstallAsync(string script, string expectedSha256, CancellationToken cancellationToken = default)
    {
        VerifyScriptHash(script, expectedSha256);
        return RunAsync(WslCommandAllowlist.RuntimeInstallScript(_options.DistroName, script, _options.ScriptCommandTimeout), cancellationToken);
    }

    public async Task<IReadOnlyList<WslCommandResult>> RunPhaseBoundaryAsync(CancellationToken cancellationToken = default)
    {
        var terminate = await TerminateAsync(cancellationToken).ConfigureAwait(false);
        var systemRunning = await RunAsync(WslCommandAllowlist.SystemIsRunning(_options.DistroName), cancellationToken).ConfigureAwait(false);
        var initVersion = await RunAsync(WslCommandAllowlist.InitVersion(_options.DistroName), cancellationToken).ConfigureAwait(false);

        return [terminate, systemRunning, initVersion];
    }

    public Task<WslCommandResult> WakeAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.Wake(_options.DistroName), cancellationToken);
    }

    public Task<WslCommandResult> StartUserUnitAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.UserSystemctl(_options.DistroName, "start"), cancellationToken);
    }

    public Task<WslCommandResult> StopUserUnitAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.UserSystemctl(_options.DistroName, "stop"), cancellationToken);
    }

    public Task<WslCommandResult> ReadHostAgentStatusAsync(CancellationToken cancellationToken = default)
    {
        return RunAsync(WslCommandAllowlist.HostAgentCtl(_options.DistroName, "status"), cancellationToken);
    }

    public async Task ColdStartAsync(CancellationToken cancellationToken = default)
    {
        await WakeAsync(cancellationToken).ConfigureAwait(false);
        await StartUserUnitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WslCommandResult> RunAsync(WslCommand command, CancellationToken cancellationToken)
    {
        var timeout = command.Timeout ?? _options.DefaultCommandTimeout;
        var result = await _processRunner.RunAsync(new WindowsProcessRequest(_options.WslExePath, command.Arguments, command.StandardInput, timeout),
            cancellationToken).ConfigureAwait(false);

        return new WslCommandResult(command, result.ExitCode, result.StandardOutput, result.StandardError, result.TimedOut);
    }

    private static void VerifyScriptHash(string script, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);

        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("WSL phase script hash verification failed.");
        }
    }
}
