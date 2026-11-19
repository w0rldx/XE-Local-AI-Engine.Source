namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Common;

/// <summary>Hub-backed <see cref="IJobCreatedPublisher" />: broadcasts on the hub of the job's own feature.</summary>
/// <remarks>
///     Sent to <c>Clients.All</c>: the feed cannot join a group for a job it does not know yet. A transport failure is
///     logged by type and swallowed, so a hub hiccup never fails the request that created the job.
/// </remarks>
internal sealed class JobCreatedPublisher : IJobCreatedPublisher
{
    private readonly IHubContext<TrainingRunHub> _runs;
    private readonly IHubContext<DatasetGenerationHub> _datasets;
    private readonly IHubContext<ImageJobHub> _images;
    private readonly ILogger<JobCreatedPublisher> _logger;

    public JobCreatedPublisher(IHubContext<TrainingRunHub> runs,
        IHubContext<DatasetGenerationHub> datasets,
        IHubContext<ImageJobHub> images,
        ILogger<JobCreatedPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(datasets);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(logger);
        _runs = runs;
        _datasets = datasets;
        _images = images;
        _logger = logger;
    }

    public Task PublishCreatedAsync(CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken = default) =>
        BroadcastAsync(JobCreatedHubEvents.Created, kind, jobId, cancellationToken);

    public Task PublishEndedAsync(CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken = default) =>
        BroadcastAsync(JobCreatedHubEvents.Ended, kind, jobId, cancellationToken);

    private async Task BroadcastAsync(string method, CreatedJobKind kind, Guid jobId, CancellationToken cancellationToken)
    {
        var clients = kind switch
        {
            CreatedJobKind.TrainingRun or CreatedJobKind.ModelLabExperiment or CreatedJobKind.BaseArtifactDownload => _runs.Clients,
            CreatedJobKind.DatasetBuild => _datasets.Clients,
            CreatedJobKind.ImageJob => _images.Clients,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown job kind.")
        };

        try
        {
            await clients.All.SendAsync(method,
                new JobCreatedHubEvent
                {
                    Id = jobId,
                    Kind = kind.ToString()
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not announce {Method} for {Kind} {JobId} ({ErrorClass}).", method, kind, jobId, exception.GetType().Name);
        }
    }
}
