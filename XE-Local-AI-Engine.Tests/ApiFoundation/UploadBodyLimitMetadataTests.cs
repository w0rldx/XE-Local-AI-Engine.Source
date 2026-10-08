namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Globalization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A raised <c>Security:MaxUploadFileSizeMb</c> must raise the host body limit of every multipart upload route, or
///     Kestrel's default limit rejects the file before the endpoint's own friendly size check runs.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class UploadBodyLimitMetadataTests
{
    // Above the form reader's 128 MiB multipart default, so both limits are proven to follow the cap.
    private const int RaisedCapMb = 200;

    [Test]
    public async Task UploadRoutes_CarryTheConfiguredCapPlusTheMultipartEnvelope()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>
            {
                ["Security:MaxUploadFileSizeMb"] = RaisedCapMb.ToString(CultureInfo.InvariantCulture)
            }
        };

        // The test server never enforces Kestrel's body limit, so the metadata the host reads is asserted directly.
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        foreach (var routeSuffix in new[] { "chat/conversations/{conversationId}/uploads", "knowledge-base/documents" })
        {
            var routes = endpoints.Where(endpoint => endpoint.RoutePattern.RawText?.EndsWith(routeSuffix, StringComparison.Ordinal) == true
                                                     && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
                                  .ToList();

            AssertEx.Equal(expected: 1, routes.Count, $"The POST {routeSuffix} route must be mapped exactly once.");
            var expected = (RaisedCapMb * 1024L * 1024L) + (64 * 1024);
            AssertEx.Equal(expected, routes[0].Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize, routeSuffix);
            // The form reader's own 128 MiB multipart default would otherwise reject the body before the handler runs.
            AssertEx.Equal(expected, routes[0].Metadata.GetMetadata<IFormOptionsMetadata>()?.MultipartBodyLengthLimit, routeSuffix);
        }
    }
}
