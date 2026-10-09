namespace XE_Local_AI_Engine.Client;

using System.Globalization;
using System.Security.Cryptography;
using Serilog;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Hosting.Vault;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Vault;
using XE_Local_AI_Engine.Client.Services.Vault.Implementation;

public sealed partial class Program
{
    /// <summary>Exit code: the unlock page's port stayed taken after an unlock, so the real host did not start on a new origin.</summary>
    private const int UnlockPortLostExitCode = 7;

    /// <summary>Exit code: the node key does not match the database, or the database's key file is missing.</summary>
    internal const int NodeKeyCustodyExitCode = 8;

    /// <summary>Exit code: the database migration pass failed; stderr names how to restore or move the database aside.</summary>
    internal const int DatabaseMigrationFailedExitCode = 9;

    /// <summary>Exit code: the data directory is not usable (not writable or not creatable) or node-settings.json is present but unreadable.</summary>
    internal const int DataDirectoryUnusableExitCode = 10;

    /// <summary>
    ///     Unwraps a locked v2 <c>node.key</c> before the host is built (ADR 0018). A CLI one-shot unlocks from what it
    ///     was given and never serves; anything else runs the <see cref="VaultUnlockHost" /> pre-host until the operator
    ///     unlocks it.
    /// </summary>
    /// <remarks>A serve launch given XE_ADMIN_PASSWORD or <c>--admin-password-stdin</c> unlocks from it like a one-shot (headless installs).</remarks>
    /// <returns>The outcome, or exit 5 (supplied secret missing or wrong) or 0 (pre-host stopped before an unlock).</returns>
    private static async Task<VaultUnlockAttempt> UnlockVaultAsync(VaultUnlockRequestContext request)
    {
        var oneShot = request.ResetRequested || request.SetupCommand is not null || request.McpKeyRequested
                      || DesktopLaunch.GetKnowledgeDowngradeCommand(request.Args) != KnowledgeDowngradeCommand.None;
        var password = request.SetupCommand?.Password ?? (request.ResetRequested ? null : DesktopLaunch.GetCommandPassword(request.Args));
        if (oneShot || password is not null)
        {
            var keyPath = Path.Combine(request.DataDirectory, VaultFileCodec.KeyFileName);
            var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(keyPath, CancellationToken.None));
            return request.ResetRequested
                ? await UnlockWithRecoveryCodeAsync(file, request.RecoveryCode, request.StandardError)
                : await UnlockWithPasswordAsync(file, password, request.StandardError);
        }

        var outcome = await VaultUnlockHost.RunAsync(new VaultUnlockHostOptions
        {
            DataDirectory = request.DataDirectory,
            BindUrl = request.BindUrl,
            ContentRootPath = request.Builder.Environment.ContentRootPath,
            WebRootPath = request.Builder.Environment.WebRootPath,
            EnvironmentName = request.Builder.Environment.EnvironmentName,
            Version = AddNodeMcpServerExtensions.ServerVersion,
            SuppressBrowser = DesktopLaunch.ShouldSuppressBrowser(request.LaunchMode, DesktopLaunch.HasNoBrowserFlag(request.Args)),
            StandardOutput = request.StandardOutput,
            ParentLost = request.ParentLost
        }, CancellationToken.None);

        return outcome is null ? VaultUnlockAttempt.Exit(0) : VaultUnlockAttempt.From(outcome);
    }

    private static async Task<VaultUnlockAttempt> UnlockWithRecoveryCodeAsync(VaultFile file, string? recoveryCode, TextWriter standardError)
    {
        if (recoveryCode is null)
        {
            await standardError.WriteLineAsync($"This node's key is protected by the admin password. Resetting it requires the recovery code shown at setup: "
                                               + $"pipe it on stdin and add {DesktopLaunch.RecoveryCodeStdinArgument}.");
            return VaultUnlockAttempt.Exit(5);
        }

        try
        {
            return VaultUnlockAttempt.From(new VaultUnlockOutcome
            {
                MasterKey = VaultFileCodec.UnwrapWithRecovery(file, recoveryCode)
            });
        }
        catch (VaultUnlockException)
        {
            await standardError.WriteLineAsync("The recovery code does not unlock this node's key.");
            return VaultUnlockAttempt.Exit(5);
        }
    }

    private static async Task<VaultUnlockAttempt> UnlockWithPasswordAsync(VaultFile file, string? password, TextWriter standardError)
    {
        if (password is null)
        {
            await standardError.WriteLineAsync($"This node's key is locked. Provide the admin password in {DesktopLaunch.AdminPasswordEnvironmentVariable} "
                                               + $"or on stdin with {DesktopLaunch.AdminPasswordStdinArgument}.");
            return VaultUnlockAttempt.Exit(5);
        }

        try
        {
            return VaultUnlockAttempt.From(new VaultUnlockOutcome
            {
                MasterKey = VaultFileCodec.UnwrapWithPassword(file, password)
            });
        }
        catch (VaultUnlockException)
        {
            await standardError.WriteLineAsync("The admin password does not unlock this node's key.");
            return VaultUnlockAttempt.Exit(5);
        }
    }

    /// <summary>Layers the unwrapped secret in as the in-memory operator secret, marks the vault unlocked and zeroes the buffer.</summary>
    private static void InjectUnlockedSecret(IConfigurationBuilder configuration, VaultUnlockOutcome outcome)
    {
        configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(outcome.MasterKey),
            [NodeVault.UnlockedConfigurationKey] = "true"
        });
        CryptographicOperations.ZeroMemory(outcome.MasterKey);
    }

    /// <summary>
    ///     Waits for the unlock page's port to come free after the pre-host stopped. The real host must keep that origin:
    ///     the shell read <c>ready.json</c> once and the SPA tab polls its own origin, so a new port would strand both.
    /// </summary>
    /// <returns><c>false</c>, after telling the operator, when the port stayed taken; the caller then exits <see cref="UnlockPortLostExitCode" />.</returns>
    private static async Task<bool> RebindUnlockPagePortAsync(string boundUrl, TextWriter standardError)
    {
        var port = new Uri(boundUrl).Port;
        if (await DesktopPortStore.WaitForPortAsync(port,
                DesktopPortStore.RebindProbeAttempts,
                DesktopPortStore.IsPortAvailable,
                static () => Task.Delay(DesktopPortStore.RebindProbeInterval, CancellationToken.None)))
        {
            return true;
        }

        Log.Error("Port {Port} of the vault unlock page did not come free again after the unlock; the engine stops instead of moving to a new origin.",
            port);
        await standardError.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Port {port} of the vault unlock page is still in use after the unlock, so the engine stopped instead of moving to a new address. Start the engine again."));
        return false;
    }

    /// <summary>Completes a recovery-code unlock: the admin password and the vault's password wrap are reset together.</summary>
    private static async Task ResetRecoveredAdminPasswordAsync(IServiceProvider services, VaultUnlockOutcome outcome)
    {
        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<INodeAuthService>()
                                .ResetAdminPasswordAsync(outcome.ResetPassword!, outcome.ResetRecoveryCode, outcome.ResetNewRecoveryCode, CancellationToken.None);
        if (result.Succeeded)
        {
            Log.Information("Admin password reset with the recovery code; sign in with the new password.");
        }
        else
        {
            Log.Error(
                "The recovery-code unlock succeeded but the admin password reset failed; the previous password and recovery code still apply, and the new recovery code shown on the unlock page is void: {Errors}",
                string.Join(" ", result.Errors));
        }
    }

    /// <summary>Everything <see cref="UnlockVaultAsync" /> needs from <c>CreateAppCoreAsync</c>.</summary>
    private sealed class VaultUnlockRequestContext
    {
        public required string[] Args { get; init; }

        public required string DataDirectory { get; init; }

        public required string BindUrl { get; init; }

        public required WebApplicationBuilder Builder { get; init; }

        public required LaunchMode LaunchMode { get; init; }

        public required SetupCommand? SetupCommand { get; init; }

        public required bool McpKeyRequested { get; init; }

        public required bool ResetRequested { get; init; }

        public required string? RecoveryCode { get; init; }

        public required TextWriter StandardOutput { get; init; }

        public required TextWriter StandardError { get; init; }

        public required Task? ParentLost { get; init; }
    }

    private sealed class VaultUnlockAttempt
    {
        public VaultUnlockOutcome? Outcome { get; private init; }

        public int? ExitCode { get; private init; }

        public static VaultUnlockAttempt From(VaultUnlockOutcome outcome) =>
            new()
            {
                Outcome = outcome
            };

        public static VaultUnlockAttempt Exit(int exitCode) =>
            new()
            {
                ExitCode = exitCode
            };
    }
}
