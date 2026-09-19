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
using XE_Local_AI_Engine.Client.Services.NodeSettings;
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

        var result = await authService.ResetAdminPasswordAsync(newPassword, CancellationToken.None);
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
        var validation = await new NodeSetupRequestValidator().ValidateAsync(request, CancellationToken.None);
        if (!validation.IsValid)
        {
            foreach (var failure in validation.Errors)
            {
                await standardError.WriteLineAsync(failure.ErrorMessage);
            }

            return 3;
        }

        await using var scope = services.CreateAsyncScope();
        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();
        NodeSetupResult result;
        try
        {
            result = await authService.SetupAsync(command.Email, command.Password, CancellationToken.None);
        }
        catch (NodeSettingsUnreadableException exception)
        {
            // The HTTP setup endpoint gets this mapped to a 400 by DomainValidationExceptionHandler; the CLI has no such
            // mapper, so without this catch a corrupt node-settings.json would take --setup out through the top-level
            // fatal handler instead of the documented exit code 5.
            await standardError.WriteLineAsync(exception.Message);
            return 5;
        }

        if (result.AlreadyInitialized)
        {
            await standardOutput.WriteLineAsync("XE_SETUP=already-configured");
            return 0;
        }

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                await standardError.WriteLineAsync(error);
            }

            return 5;
        }

        await standardOutput.WriteLineAsync("XE_SETUP=created");
        await standardOutput.WriteLineAsync($"XE_ADMIN_EMAIL={command.Email}");
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
        var status = await authService.GetStatusAsync(new ClaimsPrincipal(), CancellationToken.None);
        if (status.SetupRequired)
        {
            await standardError.WriteLineAsync("An administrator account must be configured before an MCP key can be generated.");
            return 5;
        }

        await standardError.WriteLineAsync("warning: this invalidates any previously configured MCP client's key.");
        var apiKeyService = serviceScope.ServiceProvider.GetRequiredService<IMcpServerApiKeyService>();
        var generated = await apiKeyService.GenerateAsync(scope, CancellationToken.None);
        await standardOutput.WriteLineAsync($"XE_MCP_KEY={generated.Key}");
        return 0;
    }

    private static async Task WriteHelpAsync(TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        await standardOutput.WriteLineAsync("XE Local AI Engine");
        await standardOutput.WriteLineAsync("Serve: --desktop | --mcp-only [--no-browser] [--port <1-65535>]");
        await standardOutput
              .WriteLineAsync("Commands: --setup [--admin-email <email>] [--admin-password <password> | --admin-password-stdin] | --mcp-key <delegate|agentic> | --status [--json] | --help");
        await standardOutput.WriteLineAsync("Maintenance: --reset-admin-password <password> | --knowledge-downgrade-preflight | --knowledge-downgrade-export");
        await standardOutput
              .WriteLineAsync(
                  "Credentials: scripts and installers must use XE_ADMIN_PASSWORD or --admin-password-stdin, never --admin-password on argv; argv exposes the password in process listings.");
        await standardOutput.WriteLineAsync("Data: XE_DATA_DIR must be an absolute path; status inspection never creates it.");
        await standardOutput.WriteLineAsync("Exit codes: 0 success; 1 stopped/unexpected failure; 2 usage; 3 validation; 4 instance busy; 5 setup/command failure; 6 requested port unavailable.");
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
            await standardError.WriteLineAsync(dataDirectoryError);
            return await WriteStatusAsync(args,
                new EngineStatus { Running = false, Version = null, Url = null, McpUrl = null, DataDir = string.Empty, SetupRequired = null, InstallKind = ResolveInstallKind(isManagedInstall) },
                standardOutput);
        }

        // CancellationToken.None throughout this command: status runs pre-DI, with no host and no token to inherit,
        // and every remote call below is already bounded by the 2 s client timeout before the process exits.
        var evidence = await DesktopPortStore.ReadReadyEvidenceAsync(dataDirectory, CancellationToken.None);
        if (evidence.State == ReadyEvidenceState.Invalid)
        {
            await standardError.WriteLineAsync("The readiness file is invalid or unreadable.");
        }

        var ready = evidence.Info;
        var running = ready is not null && IsProcessRunning(ready.Pid);
        bool? setupRequired = null;

        if (running && ready is not null)
        {
            try
            {
                using var injectedClient = httpClientFactory?.Invoke();
                // Pre-DI: this command runs before the host is built so a status check never takes the instance lease
                // (see the call site's own comment in Program.cs); no IHttpClientFactory exists yet, and the process
                // exits right after, so a raw client here is not a pooling concern.
                using var fallbackClient = injectedClient is null ? new HttpClient() : null;
                var client = injectedClient ?? fallbackClient!;
                client.Timeout = TimeSpan.FromSeconds(2);
                using var readyResponse = await client.GetAsync(new Uri(new Uri(ready.Url), "/health/ready"), CancellationToken.None);
                running = readyResponse.IsSuccessStatusCode;
                if (running)
                {
                    using var authResponse = await client.GetAsync(new Uri(new Uri(ready.Url), "/api/local/v1/auth/status"), CancellationToken.None);
                    if (!authResponse.IsSuccessStatusCode)
                    {
                        running = false;
                    }
                    else
                    {
                        var authStatus = await authResponse.Content.ReadFromJsonAsync<NodeAuthStatusResponse>(CancellationToken.None);
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

        var status = new EngineStatus
        {
            Running = running,
            Version = ready?.Version,
            Url = ready?.Url,
            McpUrl = ready?.McpUrl,
            DataDir = dataDirectory,
            SetupRequired = running ? setupRequired : null,
            InstallKind = ResolveInstallKind(isManagedInstall)
        };

        return await WriteStatusAsync(args, status, standardOutput);
    }

    private static string ResolveInstallKind(bool isManagedInstall) =>
        isManagedInstall ? "velopack-managed" : "unmanaged";

    private static async Task<int> WriteStatusAsync(string[] args, EngineStatus status, TextWriter standardOutput)
    {
        if (DesktopLaunch.HasJsonFlag(args))
        {
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(status, JsonSerializerOptions.Web));
        }
        else
        {
            await standardOutput.WriteLineAsync($"RUNNING={(status.Running ? "true" : "false")}");
            await standardOutput.WriteLineAsync($"VERSION={status.Version ?? string.Empty}");
            await standardOutput.WriteLineAsync($"URL={status.Url ?? string.Empty}");
            await standardOutput.WriteLineAsync($"MCP_URL={status.McpUrl ?? string.Empty}");
            await standardOutput.WriteLineAsync($"DATA_DIR={status.DataDir}");
            var setupRequiredValue = string.Empty;
            if (status.SetupRequired is { } required)
            {
                setupRequiredValue = required ? "true" : "false";
            }

            await standardOutput.WriteLineAsync($"SETUP_REQUIRED={setupRequiredValue}");
            await standardOutput.WriteLineAsync($"INSTALL_KIND={status.InstallKind}");
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

    private sealed record EngineStatus
    {
        public required bool Running { get; init; }

        public required string? Version { get; init; }

        public required string? Url { get; init; }

        public required string? McpUrl { get; init; }

        public required string DataDir { get; init; }

        public required bool? SetupRequired { get; init; }

        public required string InstallKind { get; init; }
    }

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
                var export = await safetyService.ExportAsync(CancellationToken.None);
                preflight = export.Preflight;
                Log.Information("Knowledge downgrade backup exported to {ArtifactPath} ({ArtifactBytes} bytes, SHA-256 {ArtifactSha256}).",
                    export.ArtifactPath,
                    export.ArtifactBytes,
                    export.ArtifactSha256);
            }
            else
            {
                preflight = await safetyService.PreflightAsync(CancellationToken.None);
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
