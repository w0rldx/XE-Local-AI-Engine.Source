namespace XE_Local_AI_Engine.Tests.Scheduler;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelRecommendationCheckHandler" /> tests: parameter validation rejects an invalid
///     operation / use case / limit (throwing <see cref="ScheduledJobValidationException" />) WITHOUT invoking the
///     refresh service, a non-success refresh throws so the dispatcher records a Failed run, an OCE propagates
///     untouched, and a happy run reaches the refresh service exactly once. The descriptor wiring is also asserted.
/// </summary>
public sealed class ModelRecommendationCheckHandlerTests
{
    private const string ValidParameters =
        """{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "coding", "limit": 5, "providerName": "ollama" }""";

    [Test]
    public void Descriptor_ClaimsReservedTemplateIdAndSafeDefaults()
    {
        var (handler, _) = CreateHandler();

        AssertEx.Equal("model-recommendation-check", handler.TemplateId);
        AssertEx.Equal("model-recommendation-check", handler.Descriptor.TemplateId);
        AssertEx.Equal(SchedulerMisfirePolicy.SkipMissed, handler.Descriptor.DefaultMisfirePolicy);
        AssertEx.False(handler.Descriptor.AllowAgentCreation, "agent-created model-check schedules are out of scope.");
        AssertEx.True(handler.Descriptor.AllowManualTrigger, "operators may trigger the refresh manually.");
        AssertEx.Equal(600, handler.Descriptor.DefaultMaxRuntimeSeconds ?? 0);
        AssertEx.Equal(HistoryDetailLevel.Detailed, handler.Descriptor.HistoryDetailLevel);
        AssertEx.NotNull(handler.Descriptor.DefaultParameters);
        AssertEx.NotNull(handler.Descriptor.ParameterSchema);
    }

    [Test]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Benchmark", "useCase": "coding", "limit": 5, "providerName": "ollama" }""")]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "garbage", "useCase": "coding", "limit": 5, "providerName": "ollama" }""")]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "astrology", "limit": 5, "providerName": "ollama" }""")]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "coding", "limit": 0, "providerName": "ollama" }""")]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "coding", "limit": 9999, "providerName": "ollama" }""")]
    [Arguments("""{ "approvedImageId": "llmfit-recommender-0-9-30", "operation": "Recommend", "useCase": "coding", "limit": 5, "providerName": "azure" }""")]
    [Arguments("""{ "approvedImageId": "", "operation": "Recommend", "useCase": "coding", "limit": 5, "providerName": "ollama" }""")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task ExecuteAsync_WhenParametersInvalid_ThrowsValidationExceptionWithoutRunningRefresh(string parametersJson)
    {
        var (handler, refresh) = CreateHandler();

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => handler.ExecuteAsync(Context(parametersJson), CancellationToken.None));

        AssertEx.Equal(0, refresh.CallCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenRefreshSucceeds_InvokesRefreshExactlyOnce()
    {
        var (handler, refresh) = CreateHandler();
        refresh.Result = new ModelFitRefreshResult(Guid.NewGuid(), ModelFitRunStatus.Succeeded, 3, null);

        await handler.ExecuteAsync(Context(ValidParameters), CancellationToken.None);

        AssertEx.Equal(1, refresh.CallCount);
        AssertEx.NotNull(refresh.LastRequest);
        AssertEx.Equal("llmfit-recommender-0-9-30", refresh.LastRequest!.ApprovedImageId);
        AssertEx.Equal(ModelFitOperation.Recommend, refresh.LastRequest.Operation);
        AssertEx.Equal("coding", refresh.LastRequest.UseCase!);
        AssertEx.Equal(5, refresh.LastRequest.Limit);
        AssertEx.Equal("ollama", refresh.LastRequest.ProviderName);
    }

    [Test]
    public async Task ExecuteAsync_WhenRefreshFailed_ThrowsScheduledJobExecutionExceptionWithSanitizedError()
    {
        var (handler, refresh) = CreateHandler();
        refresh.Result = new ModelFitRefreshResult(Guid.NewGuid(), ModelFitRunStatus.Failed, 0, "The approved image is disabled.");

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => handler.ExecuteAsync(Context(ValidParameters), CancellationToken.None));

        // The operator-safe SanitizedError is surfaced verbatim so the run row / toast is actionable.
        AssertEx.Equal("The approved image is disabled.", exception.Message);
        AssertEx.Equal(1, refresh.CallCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenRefreshFailedWithNullSanitizedError_ThrowsStaticFallbackMessage()
    {
        var (handler, refresh) = CreateHandler();
        refresh.Result = new ModelFitRefreshResult(Guid.NewGuid(), ModelFitRunStatus.Failed, 0, null);

        var exception = await AssertEx.ThrowsAsync<ScheduledJobExecutionException>(() => handler.ExecuteAsync(Context(ValidParameters), CancellationToken.None));

        AssertEx.Equal("The model recommendation refresh did not succeed.", exception.Message);
        AssertEx.Equal(1, refresh.CallCount);
    }

    [Test]
    public async Task ExecuteAsync_WhenRefreshThrowsCancellation_PropagatesUntouched()
    {
        var (handler, refresh) = CreateHandler();
        refresh.ThrowCancellation = true;

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => handler.ExecuteAsync(Context(ValidParameters), CancellationToken.None));

        AssertEx.Equal(1, refresh.CallCount);
    }

    private static ScheduledJobExecutionContext Context(string? parametersJson)
    {
        return new ScheduledJobExecutionContext
        {
            ScheduledJobId = Guid.NewGuid(),
            TemplateId = ModelRecommendationCheckHandler.TemplateIdValue,
            DisplayName = "Model recommendation check",
            Parameters = parametersJson,
            FireInstanceId = "fire-1",
            ScheduledFireTimeUtc = null,
            ActualFireTimeUtc = DateTimeOffset.UnixEpoch,
            TriggeredBy = ScheduledRunTrigger.Manual,
            ReportProgressAsync = null
        };
    }

    private static (ModelRecommendationCheckHandler Handler, RecordingRefreshService Refresh) CreateHandler()
    {
        var refresh = new RecordingRefreshService();
        var services = new ServiceCollection();
        services.AddSingleton<IModelFitRefreshService>(refresh);
        services.AddSingleton(Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:-]+$"
        }));
        services.AddSingleton<ModelNameValidator>();
        services.AddSingleton<ModelFitRequestValidator>();
        var provider = services.BuildServiceProvider();

        var handler = new ModelRecommendationCheckHandler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModelRecommendationCheckHandler>.Instance);

        return (handler, refresh);
    }

    private sealed class RecordingRefreshService : IModelFitRefreshService
    {
        public int CallCount { get; private set; }
        public ModelFitRefreshRequest? LastRequest { get; private set; }
        public ModelFitRefreshResult Result { get; set; } = new(Guid.NewGuid(), ModelFitRunStatus.Succeeded, 0, null);
        public bool ThrowCancellation { get; set; }

        public Task<ModelFitRefreshResult> RefreshAsync(ModelFitRefreshRequest request,
            Func<string, int?, CancellationToken, Task>? reportProgress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (ThrowCancellation)
            {
                throw new OperationCanceledException("Scripted refresh cancellation.");
            }

            return Task.FromResult(Result);
        }
    }
}
