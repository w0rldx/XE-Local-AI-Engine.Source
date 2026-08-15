namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using TUnit.Playwright;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Base for browser-driven XE node E2E tests. Mirrors the C0re <c>E2ETestBase</c> shell
///     (headless-via-<c>HEADED</c> Chromium, tracing-on-failure to <c>test-results/traces/</c>,
///     <c>PerTestSession</c> shared fixtures, bounded parallelism) but drops C0re's identity
///     login/cookie helpers — the XE node is same-origin.
///     <para>
///         Single origin: the host serves both the API and the SPA, so
///         <see cref="FrontendBaseUrl" /> == <see cref="ApiBaseUrl" /> == the factory's ServerAddress.
///         Navigate to <see cref="NodeAppUrl" /> so the browser loads the SPA shell from the node host.
///     </para>
///     <para>
///         Attribute-free core: parallelism and sign-in identity are chosen by the two thin bases that derive
///         from it — <see cref="XESerialE2ETestBase" /> (one at a time, canonical admin, UI form login) and
///         <see cref="XEPooledE2ETestBase" /> (N at a time, leased pool user, API cookie login). Do NOT put a
///         <c>[ParallelLimiter]</c> or <c>[ParallelGroup]</c> here, or every derived test would carry two.
///     </para>
/// </summary>
// S101: "XEE2ETestBase" keeps the "XE" product prefix on "E2ETestBase"; the consecutive
// capitals are the intentional, plan-mandated harness name, not a casing mistake.
#pragma warning disable S101 // Types should be named in PascalCase
public abstract class XEE2ETestBase : PageTest
{
    private bool _tracingStarted;

    protected XEE2ETestBase()
        : base(BuildLaunchOptions("chromium"))
    {
    }

    [ClassDataSource<XENodeE2EWebApplicationFactory>(Shared = SharedType.PerTestSession)]
    public required XENodeE2EWebApplicationFactory Factory { get; init; }

    /// <summary>The host origin (API base). Same as the frontend origin — the host serves both.</summary>
    protected Uri ApiBaseUrl => new(Factory.ServerAddress, UriKind.Absolute);

    /// <summary>Frontend origin == API origin (same-origin serving).</summary>
    protected Uri FrontendBaseUrl => ApiBaseUrl;

    /// <summary>
    ///     The SPA entry point: the host root <c>{ServerAddress}</c>. Navigating here
    ///     (or to a deep link below it, e.g. <c>{NodeAppUrl}/dashboard</c>) serves <c>index.html</c>.
    ///     The React client owns root, so there is no <c>/app</c> prefix.
    /// </summary>
    protected string NodeAppUrl => Factory.ServerAddress.TrimEnd('/');

    public override string BrowserName => "chromium";

    public override BrowserNewContextOptions ContextOptions(TestContext testContext)
    {
        var options = base.ContextOptions(testContext) ?? new BrowserNewContextOptions();
        options.IgnoreHTTPSErrors = true;
        return options;
    }

    [Before(Test)]
    public void ResetWorkerEventDispatcher()
    {
        // The host registers ONE real WorkerEventDispatcher and the harness shares it via
        // SharedType.PerTestSession. Production never resets CurrentInvocation (it is only assigned),
        // so a completed Chat test would otherwise leak an invocation into InvocationsPageE2ETests'
        // empty-state assertion. Clearing it before every test keeps that isolation while letting the
        // real dispatcher complete local replies. Test-only — no production behavior change.
        if (Factory.Services.GetRequiredService<IWorkerEventDispatcher>() is WorkerEventDispatcher dispatcher)
        {
            dispatcher.ResetForTests();
        }
    }

    [Before(Test)]
    public async Task StartTracingAsync(TestContext context)
    {
        await Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
            Title = $"{GetType().Name}.{context.Metadata.TestName}"
        }).ConfigureAwait(false);
        _tracingStarted = true;
    }

    /// <summary>
    ///     Leaves the browser context authenticated: it must hold the HttpOnly refresh cookie and the page
    ///     must be off <c>/login</c>. The SPA's session-restore then re-mints the in-memory access token on
    ///     every full navigation a test performs afterwards. Non-hook (the hook below drives it) so the
    ///     derived bases can pick their identity and login transport without redeclaring TUnit hooks.
    /// </summary>
    protected abstract Task SignInAsync();

    [Before(Test)]
    public Task AuthenticateAsync()
    {
        return SignInAsync();
    }

    [After(Test)]
    public async Task StopTracingAsync(TestContext context)
    {
        if (!_tracingStarted)
        {
            return;
        }

        var testState = context.Execution.Result?.State;
        var testFailed = testState?.ToString() is "Failed" or "Errored";

        if (testFailed)
        {
            var traceDirectory = Path.Combine("test-results", "traces");
            Directory.CreateDirectory(traceDirectory);

            var tracePath = Path.Combine(traceDirectory,
                $"{GetType().Name}_{context.Metadata.TestName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");

            await Context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = tracePath
            }).ConfigureAwait(false);
            _tracingStarted = false;

            return;
        }

        await Context.Tracing.StopAsync(new TracingStopOptions()).ConfigureAwait(false);
        _tracingStarted = false;
    }

    private static BrowserTypeLaunchOptions BuildLaunchOptions(string browserName)
    {
        var headless = !string.Equals(Environment.GetEnvironmentVariable("HEADED"), "true", StringComparison.OrdinalIgnoreCase);

        // --ignore-certificate-errors is chromium-only; other browsers reject unknown launch args
        // and instead trust the dev cert via ContextOptions().IgnoreHTTPSErrors.
        return string.Equals(browserName, "chromium", StringComparison.Ordinal)
            ? new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Args = ["--ignore-certificate-errors"]
            }
            : new BrowserTypeLaunchOptions
            {
                Headless = headless
            };
    }
}
#pragma warning restore S101
