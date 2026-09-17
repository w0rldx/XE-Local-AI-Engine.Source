namespace XE_Local_AI_Engine.Tests.Endpoints.CustomTools.V1;

using System.Net;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>POST custom-tools/executable-probe</c> is an <c>IDesktopOnlyEndpoint</c>: off the desktop flag the
///     FastEndpoints registration filter drops it entirely rather than leaving it to throw a 500 for a missing desktop
///     service. A unit-test host cannot run in desktop mode (docs/agent-knowledge.md), so the ABSENCE is what this suite
///     pins — a regression that registered it headless would fail here.
///     <para>
///         Absence shows up as 405, not 404: with the POST unregistered, the literal <c>executable-probe</c> segment
///         still matches the <c>custom-tools/{customToolId}</c> template, which serves GET/PUT/DELETE only. Asserting
///         405 pins the real routing outcome; a 200 (or any success) would mean the desktop gate had come undone.
///     </para>
///     <para>
///         That 405 is what an authenticated operator sees. An anonymous caller sees 401 instead: selecting no
///         endpoint for the verb is precisely the case the authorization <c>FallbackPolicy</c> answers, and it runs
///         before routing produces its 405. Either status proves the same thing here — the POST is not registered.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ValidateExecutableEndpointTests
{
    private const string ProbeRoute = "/api/local/v1/custom-tools/executable-probe";

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task Probe_WhenHostIsNotDesktop_PostIsNotRouted()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAsOperatorAsync(Factory,
            client,
            HttpMethod.Post,
            ProbeRoute,
            new
            {
                path = "/usr/bin/list-things"
            });

        AssertEx.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Test]
    public async Task Probe_WhenAnonymous_IsNotReachableEither()
    {
        using var client = Factory.CreateClient();

        using var response = await CustomToolEndpointPayloads.SendAnonymousAsync(client,
            HttpMethod.Post,
            ProbeRoute,
            new
            {
                path = "/usr/bin/list-things"
            });

        // The desktop filter still leaves the POST unregistered, but 405 is no longer what an ANONYMOUS caller
        // sees: routing selects no endpoint for this verb, and the FallbackPolicy answers that case with a
        // challenge before routing's 405 is ever produced. The operator-token sibling above still gets the 405,
        // which is what keeps this pair proving the desktop gate rather than only the authorization one.
        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
