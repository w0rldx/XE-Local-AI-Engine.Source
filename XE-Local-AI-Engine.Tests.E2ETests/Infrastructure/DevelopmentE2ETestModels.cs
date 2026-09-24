namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using Microsoft.Extensions.AI;
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
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        _ = await tools.WriteFileAsync("feature.txt", "implemented by Development E2E\n", cancellationToken);
        return new DevelopmentCoderModelResult
        {
            Submission = new DevelopmentCoderSubmission
            {
                Summary = "Implemented the deterministic E2E feature file.",
                ChangedFiles = ["feature.txt"],
                CommandIds = [],
                Notes = null
            },
            InputTokens = 10,
            OutputTokens = 10
        };
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
        return Task.FromResult(new DevelopmentReviewerModelResult
        {
            Submission = new DevelopmentReviewerSubmission
            {
                Disposition = DevelopmentReviewDisposition.Approved,
                Summary = "The validated E2E subject satisfies the acceptance criterion.",
                Findings = []
            },
            InputTokens = 10,
            OutputTokens = 10
        });
    }
}
