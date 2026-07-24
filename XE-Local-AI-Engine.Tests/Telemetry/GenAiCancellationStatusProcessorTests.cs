namespace XE_Local_AI_Engine.Tests.Telemetry;

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A gen_ai span that failed only because a user pressed Stop must exit the processor with a non-Error
///     status (so a cancelled turn is not counted as a service fault), while a genuine failure and a cancellation on a
///     non-gen_ai source are left untouched.
/// </summary>
public sealed class GenAiCancellationStatusProcessorTests
{
    [Test]
    public void OnEnd_CancelledGenAiSpan_ViaErrorTypeTag_DowngradesErrorToUnset()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var activity = StartRecorded(source, "chat");
                activity.SetStatus(ActivityStatusCode.Error, "The operation was canceled.");
                activity.SetTag("error.type", "System.OperationCanceledException");

                using (var processor = new GenAiCancellationStatusProcessor())
                {
                    processor.OnEnd(activity);
                }

                AssertEx.Equal(ActivityStatusCode.Unset, activity.Status);
                AssertEx.Equal("System.OperationCanceledException", activity.GetTagItem("error.type") as string);
            }
    }

    [Test]
    public void OnEnd_CancelledGenAiSpan_ViaExceptionEvent_DowngradesErrorToUnset()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var activity = StartRecorded(source, "chat");
                activity.SetStatus(ActivityStatusCode.Error);
                activity.AddException(new TaskCanceledException("stopped"));

                using (var processor = new GenAiCancellationStatusProcessor())
                {
                    processor.OnEnd(activity);
                }

                AssertEx.Equal(ActivityStatusCode.Unset, activity.Status);
            }
    }

    [Test]
    public void OnEnd_GenuineFailure_LeavesErrorStatusUntouched()
    {
        var (source, listener) = GenAiSource();
        using (source)
            using (listener)
            {
                using var activity = StartRecorded(source, "chat");
                activity.SetStatus(ActivityStatusCode.Error, "boom");
                activity.SetTag("error.type", "System.InvalidOperationException");

                using (var processor = new GenAiCancellationStatusProcessor())
                {
                    processor.OnEnd(activity);
                }

                AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
            }
    }

    [Test]
    public void OnEnd_CancelledButNonGenAiSource_LeavesErrorStatusUntouched()
    {
        using var source = new ActivitySource("XE.Node");
        using var listener = ListenerFor(source.Name);
        ActivitySource.AddActivityListener(listener);

        using var activity = StartRecorded(source, "node.op");
        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag("error.type", "System.OperationCanceledException");

        using (var processor = new GenAiCancellationStatusProcessor())
        {
            processor.OnEnd(activity);
        }

        AssertEx.Equal(ActivityStatusCode.Error, activity.Status);
    }

    private static (ActivitySource Source, ActivityListener Listener) GenAiSource()
    {
        var source = new ActivitySource("Microsoft.Extensions.AI");
        var listener = ListenerFor(source.Name);
        ActivitySource.AddActivityListener(listener);
        return (source, listener);
    }

    private static ActivityListener ListenerFor(string sourceName)
    {
        return new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref _) => ActivitySamplingResult.AllDataAndRecorded
        };
    }

    private static Activity StartRecorded(ActivitySource source, string name)
    {
        var activity = source.StartActivity(name);
        AssertEx.NotNull(activity, "Expected the listener to create a recorded activity.");
        return activity!;
    }
}
