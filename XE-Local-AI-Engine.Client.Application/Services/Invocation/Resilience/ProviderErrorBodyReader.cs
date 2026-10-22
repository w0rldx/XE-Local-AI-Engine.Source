namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.ClientModel;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>Reads the status and body of the first <see cref="ClientResultException" /> in an exception chain.</summary>
/// <remarks>
///     A chain holding an <see cref="AzureFoundryProviderException" /> yields null, as the failure classifier's status
///     arms skip it: the Azure translator owns those failures.
/// </remarks>
internal sealed class ProviderErrorBodyReader : IProviderErrorBodyReader
{
    public ProviderErrorBody? Read(Exception exception)
    {
        return Find(exception);
    }

    /// <inheritdoc cref="Read" />
    public static ProviderErrorBody? Find(Exception exception)
    {
        ClientResultException? statusFailure = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AzureFoundryProviderException)
            {
                return null;
            }

            statusFailure ??= current as ClientResultException;
        }

        return statusFailure is null
            ? null
            : new ProviderErrorBody
            {
                Status = statusFailure.Status,
                Body = statusFailure.GetRawResponse()?.Content?.ToMemory() ?? default
            };
    }
}
