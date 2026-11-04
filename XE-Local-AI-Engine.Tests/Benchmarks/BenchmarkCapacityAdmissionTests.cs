namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class BenchmarkCapacityAdmissionTests
{
    [Test]
    public async Task Admit_WhenTheModelIsNotInstalled_FailsOnTheFirstDecisionWithoutACapacityClaim()
    {
        const string notInstalled = "Model 'absent' is not installed on this node.";
        var capacity = Substitute.For<ICapacityService>();
        capacity.DecideAsync(Arg.Any<CapacityRequest>(), Arg.Any<CancellationToken>()).Returns(new CapacityDecision
        {
            Verdict = CapacityVerdict.RejectInsufficient,
            Reason = notInstalled,
            IsNotInstalled = true
        });
        var budget = new BenchmarkWaitBudget(new BenchmarkAdmissionRetry
        {
            MaxRetries = 3,
            Interval = TimeSpan.Zero
        });

        var exception = await AssertEx.ThrowsAsync<BenchmarkExecutionException>(() => BenchmarkCapacityAdmission.AdmitAsync(capacity,
            new CapacityRequest
            {
                ModelName = "absent",
                Role = ModelRole.Chat
            },
            new BenchmarkAdmissionContext
            {
                RunId = Guid.NewGuid(),
                Phase = "primary",
                RequestedContextTokens = 8192,
                KvCacheType = "f16",
                RejectedMessage = "The benchmark could not reserve enough local model memory."
            },
            budget,
            NullLogger.Instance,
            CancellationToken.None));

        AssertEx.Equal(notInstalled, exception.Message);
        AssertEx.Equal(3, budget.Remaining);
        await capacity.Received(1).DecideAsync(Arg.Any<CapacityRequest>(), Arg.Any<CancellationToken>());
    }
}
