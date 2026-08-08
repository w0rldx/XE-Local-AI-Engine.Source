namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Binds <see cref="ChatStreamBudgetOptions" /> — how much of one streaming turn may be buffered or deferred.
///     Separate from <c>AddNodeChat</c> because the budget is cross-cutting over the send, regenerate and resume paths
///     rather than a service any one of them owns.
/// </summary>
internal static class AddNodeChatStreamBudgetExtensions
{
    public static IHostApplicationBuilder AddNodeChatStreamBudget(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<ChatStreamBudgetOptions>()
               .Bind(configuration.GetSection(ChatStreamBudgetOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();

        return builder;
    }
}
