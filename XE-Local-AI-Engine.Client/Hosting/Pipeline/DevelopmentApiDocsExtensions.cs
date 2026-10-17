namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using FastEndpoints.Swagger;
using Scalar.AspNetCore;

/// <summary>
///     The non-production API documentation surfaces: the OpenAPI document middleware, which sits ahead of
///     authentication, and the anonymous Scalar reference page mapped after the API routes.
/// </summary>
internal static class DevelopmentApiDocsExtensions
{
    internal static WebApplication UseDevOpenApiDocument(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Ahead of authentication ON PURPOSE: UseSwaggerGen is raw middleware with no endpoint for .AllowAnonymous() to attach to, and the early position is how the
        // dev-only document is served unauthenticated, not an accident. See docs/wiki/09-api-and-hubs.md ("Dev-only surfaces").
        if (!app.Environment.IsProduction())
        {
            app.UseSwaggerGen(static options =>
            {
                options.Path = "/openapi/local/v1/{documentName}.json";
            });
        }

        return app;
    }

    internal static WebApplication MapDevApiReference(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!app.Environment.IsProduction())
        {
            app.MapScalarApiReference("/scalar", static settings =>
            {
                settings.OpenApiRoutePattern = "/openapi/local/{documentName}/{documentName}.json";

                settings.AddDocument("v1");

                settings.AddPreferredSecuritySchemes("Bearer");
            }).AllowAnonymous();
        }

        return app;
    }
}
