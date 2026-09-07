namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The body cap on a REAL connection, which is the only place it behaves the way it ships. The in-memory test host
///     honours neither <c>IRequestSizeLimitMetadata</c> nor the read that enforces it, so every unit test of the cap
///     exercises the endpoint's own Content-Length exit and none of them can see what a browser gets.
///     <para>
///         What a browser gets is Kestrel's: it enforces the cap as it READS, inside model binding, before any handler
///         code runs. Its refusal is a <c>BadHttpRequestException</c> carrying 413, and until it was mapped the routes
///         that DECLARE a 413 answered 500 on the one path that reaches them. No browser here — the assertion is about
///         the transport, so an HttpClient is the whole of what it needs.
///     </para>
///     <para>
///         Over the graph-workflow create route at its 1 MiB cap rather than the development-workflow one at 2 MiB:
///         this fixture enables graph workflows and not development workflows, and the mapping is transport-level, so
///         one capped route proves it for every capped route.
///     </para>
/// </summary>
public sealed class RequestBodyLimitE2ETests
{
    /// <summary>One byte past <c>GraphWorkflowRequestSizeLimit.MaxBytes</c>, which is internal to the host assembly.</summary>
    private const int OverTheGraphWorkflowCap = (1024 * 1024) + 1;

    private const string Definitions = "/api/local/v1/graph-workflows/definitions";

    [Test]
    public async Task ACappedRoute_WithABodyOverTheCap_Answers413RatherThanTheHostsDefault500()
    {
        var webRoot = Directory.CreateTempSubdirectory("xe-e2e-webroot-").FullName;

        // The candidate port is not a reservation, so another process can take it between the probe and Kestrel's
        // real bind; retry that one signal with a fresh candidate, exactly as the host boot smoke does.
        var bound = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var factory = new XENodeE2EWebApplicationFactory(candidate, webRoot);
            try
            {
                await factory.InitializeAsync();
            }
            catch (Exception exception) when (IsAddressInUse(exception))
            {
                await factory.DisposeAsync();
                return null;
            }

            return factory;
        });

        await using var host = bound;

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host.ServerAddress.TrimEnd('/')}{Definitions}")
        {
            // Deliberately not a definition: the body never finishes being read, so its shape is never reached. A
            // valid graph here would assert the same thing and hide which layer answered.
            Content = new StringContent(new string('a', OverTheGraphWorkflowCap), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(JwtBearerDefaults.AuthenticationScheme, await OperatorTokenAsync(host));

        using var response = await client.SendAsync(request);

        await Assert.That((int)response.StatusCode)
                    .IsEqualTo(413)
                    .Because("a route that declares a 413 must answer one when the host refuses the body, not the catch-all 500.");
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
    }

    /// <summary>
    ///     An operator token minted from the seeded admin rather than driven through the login form: the security stamp
    ///     in the token is checked against the stored user, so a synthesized principal would be rejected.
    /// </summary>
    private static async Task<string> OperatorTokenAsync(XENodeE2EWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<NodeUser>>();
        var admin = await users.FindByEmailAsync(XENodeE2EWebApplicationFactory.AdminEmail)
                    ?? throw new InvalidOperationException($"E2E admin '{XENodeE2EWebApplicationFactory.AdminEmail}' was not seeded; cannot authenticate.");

        return scope.ServiceProvider.GetRequiredService<INodeTokenService>()
                    .CreateAccessToken(admin, [NodeAuthorizationPolicies.AdminRole])
                    .AccessToken;
    }

    /// <summary>
    ///     Kestrel reports a taken port as <see cref="AddressInUseException" />, wrapped by <see cref="IOException" />
    ///     from the address binder and possibly further by the host builder, so the whole chain is searched.
    /// </summary>
    private static bool IsAddressInUse(Exception exception) =>
        exception switch
        {
            AddressInUseException => true,
            AggregateException aggregate => aggregate.InnerExceptions.Any(IsAddressInUse),
            { InnerException: { } inner } => IsAddressInUse(inner),
            _ => false
        };
}
