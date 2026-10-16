namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.ExceptionHandling;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>Maps every FastEndpoints endpoint under the local API prefix with the deny-by-default Operator policy.</summary>
internal static class FastEndpointsPipelineExtensions
{
    internal static WebApplication UseNodeFastEndpoints(this WebApplication app, bool isLocalMode)
    {
        ArgumentNullException.ThrowIfNull(app);

        // A default validator message names the camelCase wire field the client sent, not the spaced C# property name. This is process-wide.
        // A rule's own name or message still wins, and a rule over no single member keeps the library's default display name.
        ValidatorOptions.Global.DisplayNameResolver = static (_, member, _) => member is null ? null : JsonNamingPolicy.CamelCase.ConvertName(member.Name);

        app.UseFastEndpoints(config =>
        {
            config.Endpoints.RoutePrefix = LocalApiRoutes.Prefix;

            // Deny by default: every discovered endpoint gets the Operator policy whether or not its own Configure() asked for it, so a forgotten Policies() call can
            // never ship an anonymous route. See docs/wiki/09-api-and-hubs.md ("Security middleware & auth ordering") for why it is unconditional and what it costs.
            config.Endpoints.Configurator = ep => ep.Policies(NodeAuthorizationPolicies.Operator);

            // Desktop-only app self-update surface: off the flag these routes are absent rather than 500ing on a missing service. This filter cannot gate an endpoint whose
            // SERVICES are conditionally registered, because FastEndpoints instantiates every discovered endpoint before evaluating it — that is EndpointDiscoveryOptions.Filter's job.
            config.Endpoints.Filter = ep => isLocalMode || !typeof(IDesktopOnlyEndpoint).IsAssignableFrom(ep.EndpointType);

            // Single source of truth for OpenAPI operationIds, consumed by the generated hey-api React SDK: a camelCase name derived from the endpoint class name. Applied
            // globally, not per-endpoint, so FastEndpoints' type-safe Send.CreatedAtAsync<TEndpoint>() Location resolution keeps working through this same generator.
            config.Endpoints.NameGenerator = static ctx =>
            {
                var name = ctx.EndpointType.Name;
                if (name.EndsWith("Endpoint", StringComparison.Ordinal) && name.Length > "Endpoint".Length)
                {
                    name = name[..^"Endpoint".Length];
                }

                return char.ToLowerInvariant(name[0]) + name[1..];
            };

            // W3C trace id in validator 400s too (FE's default builder uses the connection id no log line carries).
            config.Errors.UseProblemDetails(static problemDetails => problemDetails.ResponseBuilder = FastEndpointsProblemWriter.Build);
            ConfigureServices.ConfigureJsonSerializerOptions(config.Serializer.Options);
        });

        return app;
    }
}
