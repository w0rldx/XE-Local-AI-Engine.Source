namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Development;

internal sealed class DevelopmentE2ECoderModel : IDevelopmentCoderModel
{
    public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        DevelopmentCloudRoleRoute? cloudRoute = null,
        CancellationToken cancellationToken = default)
    {
        liveProgress?.Output(new ChatResponseUpdate(ChatRole.Assistant, "Development E2E live output"));
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        _ = await tools.WriteFileAsync("feature.txt", "implemented by Development E2E\n", cancellationToken).ConfigureAwait(false);
        return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Implemented the deterministic E2E feature file.",
                ["feature.txt"],
                [],
                Notes: null),
            InputTokens: 10,
            OutputTokens: 10);
    }
}

internal sealed class DevelopmentE2EReviewerModel : IDevelopmentReviewerModel
{
    public Task<DevelopmentReviewerModelResult> RunAsync(string modelId,
        string prompt,
        IDevelopmentWorkspaceTools tools,
        int maxOutputTokens,
        int maxToolCalls,
        DevelopmentAttemptLiveProgress? liveProgress = null,
        DevelopmentCloudRoleRoute? cloudRoute = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new DevelopmentReviewerModelResult(new DevelopmentReviewerSubmission(DevelopmentReviewDisposition.Approved,
                "The validated E2E subject satisfies the acceptance criterion.",
                []),
            InputTokens: 10,
            OutputTokens: 10));
    }
}
