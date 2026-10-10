namespace XE_Local_AI_Engine.Tests.Middleware;

using Microsoft.AspNetCore.Http;
using NSubstitute;
using XE_Local_AI_Engine.Client.Middleware;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     <see cref="FeatureSwitchMiddleware" /> answers 404 for a switched-off feature's route family, keeps each capability GET
///     reachable, and reads the switch per request; the scheduler gate reads the startup value instead.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class FeatureSwitchMiddlewareTests
{
    [Test]
    [Arguments("/api/local/v1/development/projects")]
    [Arguments("/api/local/v1/development/hub/negotiate")]
    [Arguments("/api/local/v1/work-sessions/00000000-0000-0000-0000-000000000001")]
    [Arguments("/api/local/v1/development-workflows/runs")]
    [Arguments("/api/local/v1/graph-workflows/definitions")]
    [Arguments("/api/local/v1/transcription/runtime")]
    [Arguments("/api/local/v1/transcription/capability")]
    [Arguments("/api/local/v1/external-apps/hub/negotiate")]
    [Arguments("/api/local/v1/scheduler/jobs")]
    [Arguments("/api/local/v1/scheduler/hub/negotiate")]
    public async Task EachFamily_AnswersNotFound_WhileItsSwitchIsOff(string path)
    {
        var (status, nextCalled) = await InvokeAsync(path, AllOff().Build(), startup: new NodeStartupSettings { SchedulerEnabled = false });

        AssertEx.Equal(StatusCodes.Status404NotFound, status, path);
        AssertEx.False(nextCalled, "a switched-off feature must not reach security, routing or the endpoint");
    }

    [Test]
    [Arguments("/api/local/v1/development/capability")]
    [Arguments("/api/local/v1/work-sessions/capability")]
    [Arguments("/api/local/v1/development-workflows/capability")]
    [Arguments("/api/local/v1/graph-workflows/capability")]
    public async Task TheCapabilityGet_StaysReachable_WhileItsSwitchIsOff(string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, AllOff().Build());

        AssertEx.True(nextCalled, $"{path} is how the SPA learns the feature is off");
    }

    [Test]
    [Arguments("DELETE", "/api/local/v1/transcription/sessions/00000000-0000-0000-0000-000000000001/capture/process")]
    [Arguments("POST", "/api/local/v1/transcription/sessions/00000000-0000-0000-0000-000000000001/cancel")]
    [Arguments("POST", "/api/local/v1/transcription/models/downloads/cancel")]
    [Arguments("POST", "/api/local/v1/transcription/runtime/source-build/cancel")]
    [Arguments("POST", "/api/local/v1/transcription/runtime/eject")]
    public async Task TranscriptionStopRoutes_StayReachable_WhileTheSwitchIsOff(string method, string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, AllOff().Build(), method);

        AssertEx.True(nextCalled, $"{method} {path} ends work started before the switch went off");
    }

    [Test]
    [Arguments("POST", "/api/local/v1/transcription/sessions/00000000-0000-0000-0000-000000000001/capture/process")]
    [Arguments("POST", "/api/local/v1/transcription/sessions/00000000-0000-0000-0000-000000000001/live/start")]
    [Arguments("POST", "/api/local/v1/transcription/sessions")]
    [Arguments("GET", "/api/local/v1/transcription/sessions/00000000-0000-0000-0000-000000000001/cancel")]
    [Arguments("POST", "/api/local/v1/transcription/models/downloads")]
    public async Task TranscriptionStartRoutes_StillAnswerNotFound_WhileTheSwitchIsOff(string method, string path)
    {
        var (status, nextCalled) = await InvokeAsync(path, AllOff().Build(), method);

        AssertEx.Equal(StatusCodes.Status404NotFound, status, $"{method} {path}");
        AssertEx.False(nextCalled, "only the stop routes are carved out, matched by method as well as path");
    }

    [Test]
    [Arguments("/api/local/v1/development/projects")]
    [Arguments("/api/local/v1/work-sessions/00000000-0000-0000-0000-000000000001")]
    [Arguments("/api/local/v1/development-workflows/runs")]
    [Arguments("/api/local/v1/graph-workflows/definitions")]
    [Arguments("/api/local/v1/transcription/runtime")]
    [Arguments("/api/local/v1/external-apps")]
    [Arguments("/api/local/v1/scheduler/jobs")]
    public async Task EachFamily_PassesThrough_WhileItsSwitchIsOn(string path)
    {
        var (_, nextCalled) = await InvokeAsync(path, StubNodeRuntimeSettings.Create().Build());

        AssertEx.True(nextCalled, path);
    }

    [Test]
    public async Task ASavedChange_AppliesToTheNextRequest_WithoutARestart()
    {
        var settings = StubNodeRuntimeSettings.Create().WithWorkSessionsEnabled(false);
        var nextCalls = 0;
        var middleware = new FeatureSwitchMiddleware(_ =>
        {
            nextCalls++;
            return Task.CompletedTask;
        });
        var runtimeSettings = settings.Build();

        var first = Context("/api/local/v1/work-sessions");
        await middleware.InvokeAsync(first, runtimeSettings, new NodeStartupSettings());
        _ = settings.WithWorkSessionsEnabled(true);
        var second = Context("/api/local/v1/work-sessions");
        await middleware.InvokeAsync(second, runtimeSettings, new NodeStartupSettings());

        AssertEx.Equal(StatusCodes.Status404NotFound, first.Response.StatusCode);
        AssertEx.Equal(expected: 1, nextCalls, "the request after the switch turned on must reach the pipeline");
    }

    [Test]
    public async Task TheSchedulerGate_FollowsTheStartupValue_NotTheSavedSetting()
    {
        // The scheduler engine is registered at start, so a saved change must not hide running jobs before the restart.
        var (_, stillOpen) = await InvokeAsync("/api/local/v1/scheduler/jobs", AllOff().Build());
        var (status, _) = await InvokeAsync("/api/local/v1/scheduler/jobs", StubNodeRuntimeSettings.Create().Build(),
            startup: new NodeStartupSettings { SchedulerEnabled = false });

        AssertEx.True(stillOpen, "the saved switch alone must not close the scheduler family");
        AssertEx.Equal(StatusCodes.Status404NotFound, status, "the startup value closes it");
    }

    [Test]
    public async Task DevelopmentOff_DoesNotCloseTheDevelopmentWorkflowsFamily()
    {
        // "development" is a prefix of "development-workflows" as text but not as a path segment.
        var (_, nextCalled) = await InvokeAsync("/api/local/v1/development-workflows/runs",
            StubNodeRuntimeSettings.Create().WithDevelopmentEnabled(false).Build());

        AssertEx.True(nextCalled);
    }

    [Test]
    public async Task ARouteOutsideEveryFamily_NeverReadsASwitch()
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();

        var (_, nextCalled) = await InvokeAsync("/api/local/v1/node-settings", runtimeSettings);

        AssertEx.True(nextCalled);
        AssertEx.Empty(runtimeSettings.ReceivedCalls().ToArray(), "an unrelated request must not pay for a settings read");
    }

    private static StubNodeRuntimeSettings AllOff() =>
        StubNodeRuntimeSettings.Create()
                               .WithDevelopmentEnabled(false)
                               .WithWorkSessionsEnabled(false)
                               .WithDevWorkflowsEnabled(false)
                               .WithGraphWorkflowsEnabled(false)
                               .WithTranscriptionEnabled(false)
                               .WithExternalAppsEnabled(false)
                               .WithSchedulerEnabled(false);

    private static async Task<(int Status, bool NextCalled)> InvokeAsync(string path, INodeRuntimeSettings runtimeSettings,
        string method = "GET", NodeStartupSettings? startup = null)
    {
        var nextCalled = false;
        var middleware = new FeatureSwitchMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = Context(path);
        context.Request.Method = method;

        await middleware.InvokeAsync(context, runtimeSettings, startup ?? new NodeStartupSettings());

        return (context.Response.StatusCode, nextCalled);
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }
}
