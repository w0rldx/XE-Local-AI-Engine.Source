namespace XE_Local_AI_Engine.Client;

using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Serilog;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.Persistence;

public sealed partial class Program
{
    private static async Task<int> ResetAdminPasswordAsync(IServiceProvider services, string? newPassword)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(newPassword))
        {
            // ponytail: password passed on argv — acceptable on a local single-operator machine (the trust boundary is the
            // machine), and it avoids a console-subsystem stdin prompt that the packaged GUI exe cannot reliably show.
            Log.Error("The {Flag} flag requires a new password argument, e.g. the flag followed by <NEW_PASSWORD>.", DesktopLaunch.ResetAdminPasswordArgument);
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();

        var result = await authService.ResetAdminPasswordAsync(newPassword, CancellationToken.None).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            Log.Error("Admin password reset failed: {Errors}", string.Join(" ", result.Errors));
            return 1;
        }

        Log.Information("Admin password reset succeeded. Refresh tokens revoked and existing access tokens invalidated; "
                        + "sign in with the new password.");
        return 0;
    }

    private static async Task<int> SetupCommandAsync(IServiceProvider services,
        SetupCommand command,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        var request = new NodeSetupRequest
        {
            Email = command.Email,
            Password = command.Password
        };
        var validation = await new NodeSetupRequestValidator().ValidateAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            foreach (var failure in validation.Errors)
            {
                await standardError.WriteLineAsync(failure.ErrorMessage).ConfigureAwait(false);
            }

            return 3;
        }

        await using var scope = services.CreateAsyncScope();
        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();
        var result = await authService.SetupAsync(command.Email, command.Password, CancellationToken.None).ConfigureAwait(false);
        if (result.AlreadyInitialized)
        {
            await standardOutput.WriteLineAsync("XE_SETUP=already-configured").ConfigureAwait(false);
            return 0;
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                await standardError.WriteLineAsync(error).ConfigureAwait(false);
            }

            return 5;
        }

        await standardOutput.WriteLineAsync("XE_SETUP=created").ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"XE_ADMIN_EMAIL={command.Email}").ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> McpKeyCommandAsync(IServiceProvider services,
        McpServerApiKeyScope scope,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        await using var serviceScope = services.CreateAsyncScope();
        var authService = serviceScope.ServiceProvider.GetRequiredService<INodeAuthService>();
        var status = await authService.GetStatusAsync(new ClaimsPrincipal(), CancellationToken.None).ConfigureAwait(false);
        if (status.SetupRequired)
        {
            await standardError.WriteLineAsync("An administrator account must be configured before an MCP key can be generated.")
                               .ConfigureAwait(false);
            return 5;
        }

        await standardError.WriteLineAsync("warning: this invalidates any previously configured MCP client's key.")
                           .ConfigureAwait(false);
        var apiKeyService = serviceScope.ServiceProvider.GetRequiredService<IMcpServerApiKeyService>();
        var generated = await apiKeyService.GenerateAsync(scope, CancellationToken.None).ConfigureAwait(false);
        await standardOutput.WriteLineAsync($"XE_MCP_KEY={generated.Key}").ConfigureAwait(false);
        return 0;
    }

    private static async Task WriteHelpAsync(TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        await standardOutput.WriteLineAsync("XE Local AI Engine").ConfigureAwait(false);
        await standardOutput.WriteLineAsync("Serve: --desktop | --mcp-only [--no-browser] [--port <1-65535>]").ConfigureAwait(false);
        await standardOutput
              .WriteLineAsync("Commands: --setup [--admin-email <email>] [--admin-password <password> | --admin-password-stdin] | --mcp-key <delegate|agentic> | --status [--json] | --help")
              .ConfigureAwait(false);
        await standardOutput.WriteLineAsync("Maintenance: --reset-admin-password <password> | --knowledge-downgrade-preflight | --knowledge-downgrade-export")
                            .ConfigureAwait(false);
        await standardOutput
              .WriteLineAsync(
                  "Credentials: scripts and installers must use XE_ADMIN_PASSWORD or --admin-password-stdin, never --admin-password on argv; argv exposes the password in process listings.")
              .ConfigureAwait(false);
        await standardOutput.WriteLineAsync("Data: XE_DATA_DIR must be an absolute path; status inspection never creates it.").ConfigureAwait(false);
        await standardOutput.WriteLineAsync("Exit codes: 0 success; 1 stopped/unexpected failure; 2 usage; 3 validation; 4 instance busy; 5 setup/command failure; 6 requested port unavailable.")
                            .ConfigureAwait(false);
    }

    private static async Task<int> StatusCommandAsync(string[] args,
        bool isManagedInstall,
        TextWriter standardOutput,
        TextWriter standardError,
        Func<HttpClient>? httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        if (!DesktopBootstrap.TryResolveDataDirectoryPath(out var dataDirectory, out var dataDirectoryError))
        {
            await standardError.WriteLineAsync(dataDirectoryError).ConfigureAwait(false);
            return await WriteStatusAsync(args,
                new EngineStatus(false, null, null, null, string.Empty, null, ResolveInstallKind(isManagedInstall)),
                standardOutput).ConfigureAwait(false);
        }

        var evidence = DesktopPortStore.ReadReadyEvidence(dataDirectory);
        if (evidence.State == ReadyEvidenceState.Invalid)
        {
            await standardError.WriteLineAsync("The readiness file is invalid or unreadable.").ConfigureAwait(false);
        }

        var ready = evidence.Info;
        var running = ready is not null && IsProcessRunning(ready.Pid);
        bool? setupRequired = null;

        if (running && ready is not null)
        {
            try
            {
                using var injectedClient = httpClientFactory?.Invoke();
                using var fallbackClient = injectedClient is null ? new HttpClient() : null;
                var client = injectedClient ?? fallbackClient!;
                client.Timeout = TimeSpan.FromSeconds(2);
                using var readyResponse = await client.GetAsync(new Uri(new Uri(ready.Url), "/health/ready")).ConfigureAwait(false);
                running = readyResponse.IsSuccessStatusCode;
                if (running)
                {
                    using var authResponse = await client.GetAsync(new Uri(new Uri(ready.Url), "/api/local/v1/auth/status")).ConfigureAwait(false);
                    if (!authResponse.IsSuccessStatusCode)
                    {
                        running = false;
                    }
                    else
                    {
                        var authStatus = await authResponse.Content.ReadFromJsonAsync<NodeAuthStatusResponse>().ConfigureAwait(false);
                        if (authStatus is null)
                        {
                            running = false;
                        }
                        else
                        {
                            setupRequired = authStatus.SetupRequired;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                running = false;
            }
        }

        var status = new EngineStatus(running,
            ready?.Version,
            ready?.Url,
            ready?.McpUrl,
            dataDirectory,
            running ? setupRequired : null,
            ResolveInstallKind(isManagedInstall));

        return await WriteStatusAsync(args, status, standardOutput).ConfigureAwait(false);
    }

    private static string ResolveInstallKind(bool isManagedInstall) =>
        isManagedInstall ? "velopack-managed" : "unmanaged";

    private static async Task<int> WriteStatusAsync(string[] args, EngineStatus status, TextWriter standardOutput)
    {
        if (DesktopLaunch.HasJsonFlag(args))
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(status, JsonSerializerOptions.Web)).ConfigureAwait(false);
        }
        else
        {
            await standardOutput.WriteLineAsync($"RUNNING={(status.Running ? "true" : "false")}").ConfigureAwait(false);
            await standardOutput.WriteLineAsync($"VERSION={status.Version ?? string.Empty}").ConfigureAwait(false);
            await standardOutput.WriteLineAsync($"URL={status.Url ?? string.Empty}").ConfigureAwait(false);
            await standardOutput.WriteLineAsync($"MCP_URL={status.McpUrl ?? string.Empty}").ConfigureAwait(false);
            await standardOutput.WriteLineAsync($"DATA_DIR={status.DataDir}").ConfigureAwait(false);
            var setupRequiredValue = string.Empty;
            if (status.SetupRequired is { } required)
            {
                setupRequiredValue = required ? "true" : "false";
            }

            await standardOutput.WriteLineAsync($"SETUP_REQUIRED={setupRequiredValue}").ConfigureAwait(false);
            await standardOutput.WriteLineAsync($"INSTALL_KIND={status.InstallKind}").ConfigureAwait(false);
        }

        return status.Running ? 0 : 1;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record EngineStatus(
        bool Running,
        string? Version,
        string? Url,
        string? McpUrl,
        string DataDir,
        bool? SetupRequired,
        string InstallKind);

    private enum OneShotCommandStage
    {
        Preparation,
        Status,
        HostInitialization,
        Migrations,
        Handler
    }

    private sealed class OneShotCommandContext
    {
        internal OneShotCommandStage Stage { get; private set; } = OneShotCommandStage.Preparation;

        internal string StageOutput =>
            Stage switch
            {
                OneShotCommandStage.Preparation => "preparation",
                OneShotCommandStage.Status => "status",
                OneShotCommandStage.HostInitialization => "host-initialization",
                OneShotCommandStage.Migrations => "migrations",
                OneShotCommandStage.Handler => "handler",
                _ => "unknown"
            };

        internal void SetStage(OneShotCommandStage stage) =>
            Stage = stage;
    }

    private static async Task<int> RunKnowledgeDowngradeCommandAsync(IServiceProvider services, KnowledgeDowngradeCommand command)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            var safetyService = services.GetRequiredService<IKnowledgeDowngradeSafetyService>();
            KnowledgeDowngradePreflightResult preflight;

            if (command == KnowledgeDowngradeCommand.Export)
            {
                var export = await safetyService.ExportAsync(CancellationToken.None).ConfigureAwait(false);
                preflight = export.Preflight;
                Log.Information("Knowledge downgrade backup exported to {ArtifactPath} ({ArtifactBytes} bytes, SHA-256 {ArtifactSha256}).",
                    export.ArtifactPath,
                    export.ArtifactBytes,
                    export.ArtifactSha256);
            }
            else
            {
                preflight = await safetyService.PreflightAsync(CancellationToken.None).ConfigureAwait(false);
            }

            Log.Information("Knowledge downgrade preflight: migrationApplied={MigrationApplied}, compatible={Compatible}, "
                            + "conflictGroups={ConflictGroups}, conflictingDocuments={ConflictingDocuments}, minimumRemovals={MinimumRemovals}.",
                preflight.CollectionMigrationApplied,
                preflight.IsCompatible,
                preflight.ConflictGroupCount,
                preflight.ConflictingDocumentCount,
                preflight.MinimumDocumentsToRemove);

            foreach (var conflict in preflight.Conflicts)
            {
                Log.Warning("Knowledge downgrade {ConflictId}: opaque document identifiers {DocumentIdentifiers}.",
                    conflict.ConflictId,
                    conflict.DocumentIdentifiers);
            }

            if (!preflight.IsCompatible)
            {
                Log.Error("Knowledge downgrade is blocked. No data was modified; resolve conflicts explicitly or restore the exported backup.");
                return 3;
            }

            return 0;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Knowledge downgrade preflight/export failed. No downgrade was attempted.");
            return 1;
        }
    }
}
