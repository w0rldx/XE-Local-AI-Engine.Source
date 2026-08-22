namespace XE_Local_AI_Engine.Client.Common;

using NSwag;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

/// <summary>
///     Documents the MCP key generation request as optional. FastEndpoints must advertise <see cref="FastEndpoints.EmptyRequest" />
///     at runtime to preserve the historical bodyless POST, while scoped callers may supply the typed JSON body.
/// </summary>
internal sealed class McpServerApiKeyOpenApiOperationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var expectedPath = $"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerApiKey}";
        if (context.OperationDescription.Method != OpenApiOperationMethod.Post
            || !string.Equals(context.OperationDescription.Path, expectedPath, StringComparison.Ordinal))
        {
            return true;
        }

        var schema = context.SchemaGenerator.Generate(typeof(GenerateMcpServerApiKeyRequest), context.SchemaResolver);
        context.OperationDescription.Operation.RequestBody = new OpenApiRequestBody
        {
            IsRequired = false,
            Content =
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = schema
                }
            }
        };

        return true;
    }
}
