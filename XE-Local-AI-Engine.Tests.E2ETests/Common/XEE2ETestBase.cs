namespace XE_Local_AI_Engine.Tests.E2ETests.Common;

using Microsoft.Playwright;
using TUnit.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Base for browser-driven XE node E2E tests. Mirrors the C0re <c>E2ETestBase</c> shell
///     (headless-via-<c>HEADED</c> Chromium, tracing-on-failure to <c>test-results/traces/</c>,
///     <c>PerTestSession</c> shared fixtures, bounded parallelism) but drops C0re's identity
///     login/cookie helpers — the XE node is unpaired and same-origin.
///     <para>
///         Single origin: the host serves both the API and the SPA, so
///         <see cref="FrontendBaseUrl" /> == <see cref="ApiBaseUrl" /> == the factory's ServerAddress.
///         Navigate to <see cref="NodeAppUrl" /> (the token-injecting root route) so the browser
///         receives <c>__XE_LOCAL_OPERATOR_TOKEN__</c> and can call <c>/api/local/v1</c>.
///     </para>
/// </summary>
// S101: "XEE2ETestBase" keeps the "XE" product prefix on "E2ETestBase"; the consecutive
// capitals are the intentional, plan-mandated harness name, not a casing mistake.
#pragma warning disable S101 // Types should be named in PascalCase
[ParallelLimiter<BrowserParallelLimit>]
public abstract class XEE2ETestBase : PageTest
{
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
    ///     The token-injecting SPA entry point: the host root <c>{ServerAddress}</c>. Navigating here
    ///     (or to a deep link below it, e.g. <c>{NodeAppUrl}/dashboard</c>) serves <c>index.html</c>
    ///     through <c>ServeNodeReactIndexAsync</c>, injecting the operator token. Post-cutover the
    ///     React client owns root, so there is no <c>/app</c> prefix.
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
    public async Task StartTracingAsync(TestContext context)
    {
        await Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true,
            Title = $"{GetType().Name}.{context.Metadata.TestName}"
        }).ConfigureAwait(false);
    }

    [After(Test)]
    public async Task StopTracingAsync(TestContext context)
    {
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

            return;
        }

        await Context.Tracing.StopAsync(new TracingStopOptions()).ConfigureAwait(false);
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
