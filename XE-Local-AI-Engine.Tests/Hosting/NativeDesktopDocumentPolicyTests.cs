namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class NativeDesktopDocumentPolicyTests
{
    [Test]
    [Arguments("Mozilla/5.0 XE-Native-Restricted/1", true)]
    [Arguments("Mozilla/5.0", false)]
    [Arguments("Mozilla/5.0 XE-Native-Restricted/10", false)]
    public async Task Html_SeparatesCacheVariantsAndOnlyRestrictsNative(string userAgent, bool restricted)
    {
        using var server = await CreateServerAsync("text/html; charset=utf-8");
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        using var response = await client.GetAsync("/");

        AssertEx.True(response.Headers.Vary.Contains("User-Agent"));
        AssertEx.True(response.Headers.Vary.Contains("Accept-Encoding"));
        var policies = response.Headers.GetValues("Content-Security-Policy").ToArray();
        AssertEx.True(policies.Contains("default-src 'self'", StringComparer.Ordinal));
        AssertEx.Equal(restricted, policies.Contains(NativeDesktopDocumentPolicy.ContentPolicy, StringComparer.Ordinal));
        AssertEx.Equal(restricted, response.Headers.CacheControl?.NoStore == true);
        AssertEx.Equal(restricted, response.Headers.Contains("Permissions-Policy"));
        if (restricted)
        {
            AssertEx.Equal(NativeDesktopDocumentPolicy.PermissionsPolicy, response.Headers.GetValues("Permissions-Policy").Single());
        }

        AssertEx.Equal("document", await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task NonHtml_DoesNotChangeHeadersEvenForNativeClient()
    {
        using var server = await CreateServerAsync("application/json");
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", NativeDesktopDocumentPolicy.UserAgentMarker);
        using var response = await client.GetAsync("/");

        AssertEx.False(response.Headers.Vary.Contains("User-Agent"));
        AssertEx.False(response.Headers.Contains("Permissions-Policy"));
        AssertEx.Equal("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        AssertEx.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
    }

    [Test]
    public async Task ExistingPermissionRestriction_IsNotReplacedByNativePolicy()
    {
        using var server = await CreateServerAsync("text/html", "microphone=()");
        using var client = server.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", NativeDesktopDocumentPolicy.UserAgentMarker);
        using var response = await client.GetAsync("/");

        AssertEx.Equal("microphone=()", response.Headers.GetValues("Permissions-Policy").Single());
        AssertEx.True(response.Headers.GetValues("Content-Security-Policy").Contains(NativeDesktopDocumentPolicy.ContentPolicy, StringComparer.Ordinal));
    }

    private static Task<IHost> CreateServerAsync(string contentType, string? permissionsPolicy = null) =>
        new HostBuilder().ConfigureWebHost(builder =>
        {
            builder.UseTestServer();
            builder.Configure(app =>
            {
                app.UseMiddleware<NativeDesktopDocumentPolicy>();
                app.Run(async context =>
                {
                    context.Response.ContentType = contentType;
                    if (permissionsPolicy is not null)
                    {
                        context.Response.Headers["Permissions-Policy"] = permissionsPolicy;
                    }

                    context.Response.Headers.Append("Content-Security-Policy", "default-src 'self'");
                    context.Response.Headers.Append("Vary", "Accept-Encoding");
                    context.Response.Headers.CacheControl = "public, max-age=60";
                    await context.Response.WriteAsync("document", context.RequestAborted);
                });
            });
        }).StartAsync();
}
