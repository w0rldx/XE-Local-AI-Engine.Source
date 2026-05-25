namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     THE GATING SPIKE (plan Step 4). No browser — plain HTTP. Proves the make-or-break wiring
///     before the Playwright layer is built:
///     <list type="number">
///         <item>
///             The host serves the SPA same-origin at root (<c>/</c>) via <c>ServeNodeReactIndexAsync</c>,
///             injecting a real <c>__XE_LOCAL_OPERATOR_TOKEN__</c> (not the <c>%XE_LOCAL_OPERATOR_TOKEN%</c> sentinel).
///         </item>
///         <item>
///             An authenticated <c>GET /api/local/v1/models</c> carrying <c>X-Local-Operator</c> + a same-origin
///             <c>Origin</c> header returns 200 — proving <c>LocalApiSecurityMiddleware</c> + the operator auth handler pass.
///         </item>
///     </list>
///     If either assertion fails the downstream browser tasks must not proceed.
/// </summary>
public sealed partial class TokenInjectionSpikeE2ETests
{
    private const string SentinelToken = "%XE_LOCAL_OPERATOR_TOKEN%";

    [Test]
    [Category("Spike")]
    public async Task App_Route_Injects_Real_Token_And_Authenticated_Models_Returns_200()
    {
        // (1) Build the React client dist with VITE_API_URL baked to the pre-chosen port.
        await using var reactFixture = new XEReactClientFixture();
        await reactFixture.InitializeAsync();

        // (2) Boot the host bound to that same port, serving the fresh dist as the web root.
        await using var factory = new XENodeE2EWebApplicationFactory(reactFixture.Port, reactFixture.TempRoot);
        await factory.InitializeAsync();

        var serverAddress = factory.ServerAddress.TrimEnd('/');
        using var client = new HttpClient();

        // (3) GET / must be served by ServeNodeReactIndexAsync (token-injected), not raw UseStaticFiles.
        var appResponse = await client.GetAsync($"{serverAddress}/");
        var appBody = await appResponse.Content.ReadAsStringAsync();

        await Assert.That((int)appResponse.StatusCode).IsEqualTo(200);
        await Assert.That(appBody).Contains("globalThis.__XE_LOCAL_OPERATOR_TOKEN__");
        await Assert.That(appBody).DoesNotContain(SentinelToken);

        var token = ExtractInjectedToken(appBody);
        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Length).IsEqualTo(64); // LocalOperatorTokenProvider => 32 random bytes as hex.

        // (4) Authenticated same-origin API call: header X-Local-Operator + Origin == ServerAddress.
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"{serverAddress}/api/local/v1/models");
        apiRequest.Headers.Add("X-Local-Operator", token);
        apiRequest.Headers.Add("Origin", serverAddress);

        var apiResponse = await client.SendAsync(apiRequest);

        await Assert.That((int)apiResponse.StatusCode).IsEqualTo(200);
    }

    private static string? ExtractInjectedToken(string html)
    {
        // Matches: globalThis.__XE_LOCAL_OPERATOR_TOKEN__ = "<token>";  (value JSON-serialized => quoted).
        var match = InjectedTokenRegex().Match(html);
        return match.Success ? match.Groups["token"].Value : null;
    }

    [GeneratedRegex("__XE_LOCAL_OPERATOR_TOKEN__\\s*=\\s*\"(?<token>[^\"]+)\"")]
    private static partial Regex InjectedTokenRegex();
}
