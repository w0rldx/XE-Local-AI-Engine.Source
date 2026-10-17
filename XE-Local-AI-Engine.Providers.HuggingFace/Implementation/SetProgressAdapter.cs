namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Re-frames one part's byte counts as progress through the whole file-set.
/// </summary>
/// <remarks>
///     The underlying download client reports bytes for the single file it is transferring. Forwarding that
///     unchanged makes a three-part model fill its bar and snap back to zero twice, which reads as a failure and a
///     restart. Offsetting by the bytes already finished — and reporting the set total when every part declared a
///     size — turns it into one monotonic bar. <see cref="PullProgress.PartIndex" />/<see cref="PullProgress.PartCount" />
///     let the UI name the file being fetched.
/// </remarks>
internal sealed class SetProgressAdapter : IProgress<PullProgress>
{
    private readonly long _completedInPriorParts;
    private readonly IProgress<PullProgress> _inner;
    private readonly string _modelName;
    private readonly int _partCount;
    private readonly int _partIndex;
    private readonly long? _setTotalBytes;

    public SetProgressAdapter(IProgress<PullProgress> inner,
        string modelName,
        long completedInPriorParts,
        long? setTotalBytes,
        int partIndex,
        int partCount)
    {
        _inner = inner;
        _modelName = modelName;
        _completedInPriorParts = completedInPriorParts;
        _setTotalBytes = setTotalBytes;
        _partIndex = partIndex;
        _partCount = partCount;
    }

    public void Report(PullProgress value)
    {
        _inner.Report(new PullProgress
        {
            ModelName = _modelName,
            Status = value.Status,
            // Fall back to this part's own total only when the set total is unknown, so the bar is either
            // set-relative throughout or part-relative throughout — never a silent mix of the two.
            TotalBytes = _setTotalBytes ?? value.TotalBytes,
            CompletedBytes = _completedInPriorParts + (value.CompletedBytes ?? 0),
            PartIndex = _partIndex,
            PartCount = _partCount
        });
    }
}
