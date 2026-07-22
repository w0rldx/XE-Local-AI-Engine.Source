namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

public sealed record DevelopmentCloudRoleRoute(
    string ProviderName,
    string ModelId,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options);

/// <summary>
///     Builds a cloud role request from one approved bundle. The API accepts no chat history or general tool catalog.
/// </summary>
public sealed class DevelopmentCloudRoleRouteFactory(IDevelopmentCloudContextCatalog contextCatalog)
{
    private readonly IDevelopmentCloudContextCatalog _contextCatalog = contextCatalog ?? throw new ArgumentNullException(nameof(contextCatalog));

    public DevelopmentCloudRoleRoute Create(DevelopmentCloudContextBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        _contextCatalog.Register(bundle);

        var bundleReader = AIFunctionFactory.Create(
            (string resource) => bundle.ReadResource(resource),
            "development_read_approved_context",
            "Reads one exact resource from the immutable approved Development context bundle.");
        var options = new ChatOptions
        {
            ModelId = bundle.ModelId,
            Tools = [bundleReader]
        };
        DevelopmentCloudAuthorizationMetadata.Apply(options,
            new DevelopmentCloudAuthorizationEnvelope(DevelopmentCloudAuthorizationEnvelope.CurrentVersion,
                bundle.ProjectId,
                bundle.TaskId,
                bundle.AttemptId,
                DevelopmentExecutionPolicy.CloudScoped,
                bundle.Id,
                bundle.ContentHash,
                bundle.ExpiresAt,
                bundle.Nonce));

        ChatMessage[] messages =
        [
            new(ChatRole.System,
                "Operate only on the immutable approved Development context. Use development_read_approved_context; no repository, shell, saved-agent, or chat-history capability is available."),
            new(ChatRole.User,
                "Read the approved requirements, acceptance-criteria, policy, and only the listed excerpt resources needed for this bounded role attempt.")
        ];
        return new DevelopmentCloudRoleRoute(bundle.ProviderName, bundle.ModelId, Array.AsReadOnly(messages), options);
    }
}
