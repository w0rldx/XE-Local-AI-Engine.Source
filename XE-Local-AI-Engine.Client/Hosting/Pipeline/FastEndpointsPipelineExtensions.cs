namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using System.Text.Json;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
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
            config.Binding.JsonExceptionTransformer = ToBindingFailure;
        });

        return app;
    }

    // The serializer's messages name a CLR type (unknown property, failed conversion, missing required members); all three
    // name the wire field instead. Any other message (malformed JSON) carries no type and passes through.
    internal static ValidationFailure ToBindingFailure(JsonException exception)
    {
        const string unmappedPrefix = "The JSON property '";
        const string unmapped = "' could not be mapped to any .NET member";
        var unmappedEnd = exception.Message.IndexOf(unmapped, StringComparison.Ordinal);
        if (exception.Message.StartsWith(unmappedPrefix, StringComparison.Ordinal) && unmappedEnd > unmappedPrefix.Length)
        {
            var property = exception.Message[unmappedPrefix.Length..unmappedEnd];
            return new ValidationFailure(property, $"The request body has an unknown property: '{property}'.");
        }

        const string missingRequired = "was missing required properties including: ";
        var field = exception.Path is { Length: > 2 } path ? path[2..] : "SerializerErrors";
        var missingAt = exception.Message.IndexOf(missingRequired, StringComparison.Ordinal);
        string message;
        if (missingAt >= 0)
        {
            message = $"The request body is missing required properties: {exception.Message[(missingAt + missingRequired.Length)..].TrimEnd('.')}.";
        }
        else if (exception.Message.Contains("could not be converted to", StringComparison.Ordinal))
        {
            message = $"The value of '{field}' has the wrong JSON type or shape.";
        }
        else
        {
            message = exception.Message;
        }

        return new ValidationFailure(field, message);
    }
}
