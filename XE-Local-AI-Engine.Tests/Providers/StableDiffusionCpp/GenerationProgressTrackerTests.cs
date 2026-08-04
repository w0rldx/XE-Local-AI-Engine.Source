namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Diagnostics;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The honesty rules for the generation timeline: which stdout observations may be attributed to this job, which
///     would move the bar backwards, and — the point of the whole feature — exactly when a countdown may be shown.
/// </summary>
public sealed class GenerationProgressTrackerTests
{
    [Test]
    public void Sampling_WhileGenerating_ReportsStepsAndAnEstimateOverTheRemainingOnes()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 12, TotalSteps: 20, SecondsPerIteration: 2.0));

        var report = reports[^1];
        AssertEx.Equal(ImageGenPhase.Sampling, report.Phase);
        AssertEx.Equal(expected: 12, report.Step);
        AssertEx.Equal(expected: 20, report.TotalSteps);
        AssertEx.Equal(TimeSpan.FromSeconds(16), report.EstimatedRemaining, "8 steps left at 2s each is 16s.");
    }

    /// <summary>
    ///     The failure this feature exists to fix. After the last step the job is NOT done — the VAE decode still has to
    ///     run, and it can be a third of the wall clock. A countdown that survives into it shows "0s left" while the
    ///     image is still being written, which reads as a hang.
    /// </summary>
    [Test]
    public void Sampling_AtTheLastStep_StopsOfferingACountdown()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 20, TotalSteps: 20, SecondsPerIteration: 2.0));

        AssertEx.Null(reports[^1].EstimatedRemaining, "The last step leaves only the unmeasurable decode; no honest estimate exists.");
    }

    [Test]
    public void Decoding_KeepsTheFullBarButNeverACountdown()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 20, TotalSteps: 20, SecondsPerIteration: 2.0));

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Decoding));

        var report = reports[^1];
        AssertEx.Equal(ImageGenPhase.Decoding, report.Phase);
        AssertEx.Equal(expected: 20, report.Step, "The bar stays full through the decode rather than resetting.");
        AssertEx.Null(report.EstimatedRemaining);
    }

    /// <summary>
    ///     Attribution. The coordinator's generation slot is released in a <c>finally</c> even when the cancel path
    ///     throws something it does not catch, so the daemon can still be running an abandoned job. Only a job whose own
    ///     status says Generating may claim the steps coming off that daemon.
    /// </summary>
    [Test]
    public void Sampling_BeforeThisJobIsGenerating_IsNotAttributedToIt()
    {
        var (tracker, reports) = Build();

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 5, TotalSteps: 20, SecondsPerIteration: 1.0));

        AssertEx.Empty(reports, "A step observed while this job is still queued belongs to whatever the daemon is really running.");
    }

    /// <summary>A cold model load is worth showing before the job is submitted — it is the longest silent gap there is.</summary>
    [Test]
    public void Loading_BeforeThisJobIsGenerating_IsStillReported()
    {
        var (tracker, reports) = Build();

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Loading));

        AssertEx.Equal(expected: 1, reports.Count);
        AssertEx.Equal(ImageGenPhase.Loading, reports[0].Phase);
        AssertEx.Null(reports[0].EstimatedRemaining, "A load has no measurable rate, so it gets no countdown.");
    }

    /// <summary>The VAE weights load AFTER sampling. Reporting that as "loading" would rewind the UI to the start.</summary>
    [Test]
    public void Loading_AfterSamplingHasStarted_DoesNotRewindThePhase()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 20, TotalSteps: 20, SecondsPerIteration: 1.0));
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Decoding));

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Loading));

        AssertEx.Equal(ImageGenPhase.Decoding, reports[^1].Phase, "The decode-side weight load is part of finishing, not a restart.");
    }

    [Test]
    public void Sampling_AStepThatIsNotAhead_IsDropped()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 12, TotalSteps: 20, SecondsPerIteration: 1.0));
        var afterFirst = reports.Count;

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 11, TotalSteps: 20, SecondsPerIteration: 1.0));
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 12, TotalSteps: 20, SecondsPerIteration: 1.0));

        AssertEx.Equal(afterFirst, reports.Count, "A step at or behind the last one is a stale frame; believing it walks the bar backwards.");
    }

    /// <summary>A coarse poll must not overwrite the finer phase, or the card flickers between "sampling 5/20" and "generating".</summary>
    [Test]
    public void ReportCoarse_GeneratingAfterAFinePhase_IsNotPushed()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);
        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 5, TotalSteps: 20, SecondsPerIteration: 1.0));
        var afterSampling = reports.Count;

        tracker.ReportCoarse(ImageGenPhase.Generating, queuePosition: null);

        AssertEx.Equal(afterSampling, reports.Count);
    }

    [Test]
    public void ReportCoarse_Queued_ReportsEachChangedQueuePosition()
    {
        var (tracker, reports) = Build();

        tracker.ReportCoarse(ImageGenPhase.Queued, queuePosition: 3);
        tracker.ReportCoarse(ImageGenPhase.Queued, queuePosition: 3);
        tracker.ReportCoarse(ImageGenPhase.Queued, queuePosition: 2);

        AssertEx.Equal(expected: 2, reports.Count);
        AssertEx.Equal(expected: 2, reports[^1].QueuePosition);
        AssertEx.Null(reports[^1].EstimatedRemaining, "A queued job's wait is not this runtime's to estimate.");
    }

    /// <summary>Once the job is terminal nothing may follow it — a late step arriving after "succeeded" is noise.</summary>
    [Test]
    public void AfterATerminalReport_NothingFurtherIsPushed()
    {
        var (tracker, reports) = Build();
        tracker.SetGenerating(isGenerating: true);
        tracker.ReportCoarse(ImageGenPhase.Completed, queuePosition: null);
        var afterTerminal = reports.Count;

        tracker.ObserveFine(new SdProgressObservation(ImageGenPhase.Sampling, Step: 19, TotalSteps: 20, SecondsPerIteration: 1.0));
        tracker.ReportCoarse(ImageGenPhase.Generating, queuePosition: null);

        AssertEx.Equal(afterTerminal, reports.Count);
    }

    private static (GenerationProgressTracker Tracker, List<ImageGenProgress> Reports) Build()
    {
        var reports = new List<ImageGenProgress>();
        var progress = new DelegateProgress(reports.Add);
        return (new GenerationProgressTracker(progress, Stopwatch.GetTimestamp()), reports);
    }

    private sealed class DelegateProgress(Action<ImageGenProgress> handler) : IProgress<ImageGenProgress>
    {
        public void Report(ImageGenProgress value)
        {
            handler(value);
        }
    }
}
