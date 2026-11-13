namespace XE_Local_AI_Engine.Tests.Benchmarks;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One substitute per benchmark store role, swapped into a test host together: a role left unswapped would resolve
///     the real store, and the queue consumer the host runs claims through one of them.
/// </summary>
internal sealed class BenchmarkStoreSubstitutes
{
    public IBenchmarkProjectStore Projects { get; } = Substitute.For<IBenchmarkProjectStore>();
    public IBenchmarkTaskItemStore TaskItems { get; } = Substitute.For<IBenchmarkTaskItemStore>();
    public IBenchmarkRunStore Runs { get; } = Substitute.For<IBenchmarkRunStore>();
    public IBenchmarkQueueStore Queue { get; } = Substitute.For<IBenchmarkQueueStore>();
    public IBenchmarkJudgeStore Judging { get; } = Substitute.For<IBenchmarkJudgeStore>();
    public IBenchmarkFidelityStore Fidelity { get; } = Substitute.For<IBenchmarkFidelityStore>();
    public IBenchmarkPairwiseStore Pairwise { get; } = Substitute.For<IBenchmarkPairwiseStore>();
    public IBenchmarkReviewStore Reviews { get; } = Substitute.For<IBenchmarkReviewStore>();

    public void ReplaceIn(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton(Projects));
        services.Replace(ServiceDescriptor.Singleton(TaskItems));
        services.Replace(ServiceDescriptor.Singleton(Runs));
        services.Replace(ServiceDescriptor.Singleton(Queue));
        services.Replace(ServiceDescriptor.Singleton(Judging));
        services.Replace(ServiceDescriptor.Singleton(Fidelity));
        services.Replace(ServiceDescriptor.Singleton(Pairwise));
        services.Replace(ServiceDescriptor.Singleton(Reviews));
    }
}
