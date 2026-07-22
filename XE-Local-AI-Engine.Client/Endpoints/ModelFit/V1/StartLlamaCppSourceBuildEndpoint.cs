namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class StartLlamaCppSourceBuildEndpoint(
    ILlamaCppSourceBuildPrerequisiteProbe prerequisiteProbe,
    ILlamaCppSourceBuildService buildService,
    ILlamaServerProcessSupervisor processSupervisor) : Endpoint<StartLlamaCppSourceBuildRequest, StartLlamaCppSourceBuildResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuild);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
            .Produces<StartLlamaCppSourceBuildResponse>(StatusCodes.Status200OK)
            .ProducesProblemFE(StatusCodes.Status400BadRequest)
            .Produces<LlamaCppSourceBuildBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartLlamaCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "In-app source builds are available on Linux only.").ConfigureAwait(false);
            return;
        }

        var contract = LlamaCppSourceBuildRequestValidation.Normalize(request.ToContract());
        var report = await prerequisiteProbe.ProbeAsync(contract.Backend, ct).ConfigureAwait(false);
        if (!report.CanBuild)
        {
            var diskShort = report.Items.Any(static item => item is { Key: "free-disk", Satisfied: false });
            await BlockAsync(diskShort ? "disk" : "prerequisites",
                diskShort
                    ? "There is not enough free disk space to build the source runtime."
                    : "One or more build prerequisites are missing; resolve the checklist before building.").ConfigureAwait(false);
            return;
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (runningProcessCount > 0)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
            {
                Reason = "processes-running",
                Message = "Stop or eject all running llama.cpp models before building the runtime.",
                RunningProcessCount = runningProcessCount
            })).ConfigureAwait(false);
            return;
        }

        try
        {
            var outcome = await buildService.StartAsync(contract, ct).ConfigureAwait(false);
            if (outcome == LlamaCppSourceBuildStartOutcome.AlreadyRunning)
            {
                await BlockAsync("already-building", "A source build is already in progress.").ConfigureAwait(false);
                return;
            }

            await Send.OkAsync(new StartLlamaCppSourceBuildResponse
            {
                Started = true,
                Status = buildService.GetStatus().ToResponse()
            }, ct).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException exception)
        {
            await BlockAsync("prerequisites", exception.Message).ConfigureAwait(false);
        }
    }

    private Task BlockAsync(string reason, string message)
    {
        return Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse { Reason = reason, Message = message }));
    }
}
