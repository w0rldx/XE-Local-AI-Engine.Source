namespace XE_Local_AI_Engine.Client.Hosting.Vault;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Serilog;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1;
using XE_Local_AI_Engine.Client.Endpoints.Auth.V1.Validators;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Vault;

/// <summary>
///     The locked engine (ADR 0018): a minimal host on the real origin serving the SPA, readiness, <c>auth/status</c>
///     and the two unlock routes until the v2 <c>node.key</c> is unwrapped. Other local API routes answer 503.
/// </summary>
/// <remarks>
///     Minimal-API routes, not FastEndpoints: two anonymous routes in a host that lives until one unlock do not need
///     endpoint discovery, the deny-by-default Operator configurator or a second serializer setup. Rate limiting is one
///     <see cref="FixedWindowRateLimiter" /> owned and disposed here rather than the <c>UseRateLimiter</c> middleware,
///     whose replenishment timer would keep the stopped pre-host reachable for the life of the process.
/// </remarks>
internal static class VaultUnlockHost
{
    /// <summary>Unlock attempts (password or recovery) per window, shared by every caller: only loopback peers get this far.</summary>
    internal const int AttemptLimit = 5;

    internal static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(5);

    /// <summary>A failed attempt never answers faster than this, so a fast recovery-code check leaks no timing.</summary>
    internal static readonly TimeSpan MinimumFailureLatency = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Serves until an unlock succeeds (returns its outcome, with the URL actually bound) or the host is stopped by a
    ///     signal or the shell's pipe closing (returns <see langword="null" />, readiness file removed).
    /// </summary>
    internal static async Task<VaultUnlockOutcome?> RunAsync(VaultUnlockHostOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var keyPath = Path.Combine(options.DataDirectory, VaultFileCodec.KeyFileName);
        var file = VaultFileCodec.Read(await File.ReadAllBytesAsync(keyPath, cancellationToken));
        var unlocked = new UnlockSlot();
        await using var limiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = AttemptLimit,
            Window = AttemptWindow,
            QueueLimit = 0,
            AutoReplenishment = true
        });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = options.ContentRootPath,
            WebRootPath = options.WebRootPath,
            EnvironmentName = options.EnvironmentName
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: false);
        builder.WebHost.UseUrls(options.BindUrl);
        builder.Services.AddHealthChecks();

        await using var app = builder.Build();
        MapPipeline(app, file, limiter, unlocked);

        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stoppingRegistration = app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult());

        await app.StartAsync(cancellationToken);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(VaultUnlockHost).FullName!);
        var boundUrl = LoopbackUrlResolver.Resolve(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
                       ?? throw new InvalidOperationException("The vault unlock host bound no loopback HTTP address.");
        logger.LogInformation("The node vault is locked; serving the unlock page on {Url}.", boundUrl);
        if (!options.SuppressBrowser)
        {
            BrowserLauncher.OpenBrowser(boundUrl, OperatingSystem.IsWindows(), logger, BrowserLauncher.StartProcess);
        }

        DesktopReadyPublisher.Publish(options.DataDirectory, boundUrl, options.Version, TimeProvider.System, options.StandardOutput, logger);

        await Task.WhenAny(unlocked.Completion.Task, stopping.Task, options.ParentLost ?? Task.Delay(Timeout.Infinite, cancellationToken));

        using (var stopBudget = new CancellationTokenSource(StopBudget))
        {
            await app.StopAsync(stopBudget.Token);
        }

        if (unlocked.Completion.Task.IsCompletedSuccessfully)
        {
            var outcome = await unlocked.Completion.Task;
            return new VaultUnlockOutcome
            {
                MasterKey = outcome.MasterKey,
                BoundUrl = boundUrl.TrimEnd('/'),
                ResetPassword = outcome.ResetPassword,
                ResetRecoveryCode = outcome.ResetRecoveryCode,
                ResetNewRecoveryCode = outcome.ResetNewRecoveryCode,
                UnlockTicket = outcome.UnlockTicket
            };
        }

        logger.LogInformation("The vault unlock host stopped before the node was unlocked.");
        DesktopPortStore.DeleteReady(options.DataDirectory, logger);
        return null;
    }

    private static void MapPipeline(WebApplication app, VaultFile file, RateLimiter limiter, UnlockSlot unlocked)
    {
        app.UseNodeResponseHeaders();
        app.UseMiddleware<NativeDesktopDocumentPolicy>();
        app.UseStaticFiles(SpaStaticFileOptions.Create());
        app.UseMiddleware<LocalApiSecurityMiddleware>();
        app.UseRouting();

        // No checks while locked: the shell and --status only need the 200, as from the real host's liveness probe.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = static _ => false
        });

        var prefix = $"/{LocalApiRoutes.Prefix}/";
        app.MapGet(prefix + LocalApiRoutes.Auth.Status, static () => Results.Json(new NodeAuthStatusResponse
        {
            SetupRequired = false,
            Authenticated = false,
            Vault = NodeAuthVaultStatus.Locked
        }));

        app.MapPost(prefix + LocalApiRoutes.Auth.VaultUnlock,
            (VaultUnlockRequest request, HttpContext context) => UnlockAsync(request, context, file, limiter, unlocked));
        app.MapPost(prefix + LocalApiRoutes.Auth.VaultUnlockRecovery,
            (VaultRecoveryUnlockRequest request, HttpContext context) => UnlockWithRecoveryAsync(request, context, file, limiter, unlocked));

        // Every other local API route, any verb. Its literal prefix outranks the SPA fallback below, so the shell never
        // answers for the API and no fallback-detach middleware is needed here.
        app.Map(prefix + "{**rest}", static () => Results.Problem(title: "Vault locked",
            detail: "The node is locked. Unlock it with the admin password first.",
            statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapFallbackToFile("index.html", SpaStaticFileOptions.Create());
    }

    private static async Task<IResult> UnlockAsync(VaultUnlockRequest request,
        HttpContext context,
        VaultFile file,
        RateLimiter limiter,
        UnlockSlot unlocked)
    {
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            return TooManyAttempts();
        }

        var validation = await new VaultUnlockRequestValidator().ValidateAsync(request, context.RequestAborted);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation.Errors.Select(static failure => failure.ErrorMessage));
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var masterKey = VaultFileCodec.UnwrapWithPassword(file, request.Password);
            var ticketValue = VaultUnlockTicket.NewValue();
            return Succeeded(context, unlocked, new VaultUnlockOutcome
            {
                MasterKey = masterKey,
                UnlockTicket = new VaultUnlockTicket(ticketValue, TimeProvider.System.GetUtcNow() + VaultUnlockTicket.Lifetime)
            }, ticketValue: ticketValue);
        }
        catch (VaultUnlockException)
        {
            await DelayFailureAsync(started, context.RequestAborted);
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> UnlockWithRecoveryAsync(VaultRecoveryUnlockRequest request,
        HttpContext context,
        VaultFile file,
        RateLimiter limiter,
        UnlockSlot unlocked)
    {
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            return TooManyAttempts();
        }

        var validation = await new VaultRecoveryUnlockRequestValidator().ValidateAsync(request, context.RequestAborted);
        if (!validation.IsValid)
        {
            return ValidationFailed(validation.Errors.Select(static failure => failure.ErrorMessage));
        }

        var policyErrors = VaultPasswordPolicy.Validate(request.NewPassword);
        if (policyErrors.Count > 0)
        {
            return ValidationFailed(policyErrors);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            // Proves the code only. The password wrap and the Identity hash are reset together by the real host's
            // ResetAdminPasswordAsync, which re-proves the code and restores the file if Identity refuses.
            var masterKey = VaultFileCodec.UnwrapWithRecovery(file, request.RecoveryCode);
            // The reset rotates the code. It is minted here so this response, the only one the operator sees, can show it.
            var newRecoveryCode = VaultFileCodec.NewRecoveryCode();
            return Succeeded(context, unlocked, new VaultUnlockOutcome
            {
                MasterKey = masterKey,
                ResetPassword = request.NewPassword,
                ResetRecoveryCode = request.RecoveryCode,
                ResetNewRecoveryCode = newRecoveryCode
            }, new VaultRecoveryUnlockResponse
            {
                RecoveryCode = newRecoveryCode
            });
        }
        catch (VaultUnlockException)
        {
            await DelayFailureAsync(started, context.RequestAborted);
            return Results.Unauthorized();
        }
    }

    /// <summary>
    ///     Reserves the one unlock before answering and completes it once the response was sent. A concurrent second
    ///     success answers 409, so no recovery unlock shows a code the real host will not install.
    /// </summary>
    /// <param name="body">The 200 body for a recovery unlock; <see langword="null" /> answers 204.</param>
    /// <param name="ticketValue">The password unlock's one-time ticket, set as the <c>node_ut</c> cookie once the unlock is reserved.</param>
    private static IResult Succeeded(HttpContext context,
        UnlockSlot unlocked,
        VaultUnlockOutcome outcome,
        VaultRecoveryUnlockResponse? body = null,
        string? ticketValue = null)
    {
        if (!unlocked.TryReserve())
        {
            CryptographicOperations.ZeroMemory(outcome.MasterKey);
            return Results.Problem(title: "Unlock in progress",
                detail: "An unlock is already in progress.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (ticketValue is not null)
        {
            NodeAuthCookie.AppendUnlockTicket(context.Response, ticketValue, outcome.UnlockTicket!.ExpiresAt.UtcDateTime);
        }

        context.Response.OnCompleted(() =>
        {
            unlocked.Completion.TrySetResult(outcome);
            return Task.CompletedTask;
        });
        return body is null ? Results.NoContent() : Results.Json(body);
    }

    private static async Task DelayFailureAsync(long started, CancellationToken cancellationToken)
    {
        var remaining = MinimumFailureLatency - Stopwatch.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static IResult TooManyAttempts() =>
        Results.Json(new NodeAuthErrorResponse
        {
            Message = "Too many unlock attempts. Please try again later."
        }, statusCode: StatusCodes.Status429TooManyRequests);

    private static IResult ValidationFailed(IEnumerable<string> errors) =>
        Results.Json(new NodeAuthErrorResponse
        {
            Message = "The unlock request is invalid.",
            Errors = [.. errors]
        }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The single unlock a pre-host serves: reserved atomically by the first proven credential, completed once its response was sent.</summary>
    private sealed class UnlockSlot
    {
        private int _reserved;

        internal TaskCompletionSource<VaultUnlockOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool TryReserve() =>
            Interlocked.Exchange(ref _reserved, 1) == 0;
    }
}
