namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.ClientModel.Primitives;

/// <summary>
///     A System.ClientModel pipeline policy that appends a fixed, already-resolved custom header set to every outbound
///     Azure Foundry / Azure OpenAI request. Registered at <see cref="PipelinePosition.PerCall" /> (before retries) so
///     each static header is set once per call. No I/O and no logging — the resolved values may include secrets.
/// </summary>
internal sealed class CustomHeaderPipelinePolicy : PipelinePolicy
{
    private readonly IReadOnlyList<ResolvedCustomHeader> _headers;

    public CustomHeaderPipelinePolicy(IReadOnlyList<ResolvedCustomHeader> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        _headers = headers;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeaders(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ApplyHeaders(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void ApplyHeaders(PipelineMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        foreach (var (name, value) in _headers)
        {
            // Belt-and-suspenders vs the save-time reject: a reserved name must never override auth/transport headers.
            if (AzureFoundryHeaderRules.IsReservedName(name))
            {
                continue;
            }

            message.Request.Headers.Set(name, value);
        }
    }
}
