namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Net;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The development-workflow switch: what it refuses at startup, and what a node answers with it off.
/// </summary>
public sealed class DevWorkflowOptionsTests
{
    private const string ProbeRoute = "/api/local/v1/development-workflows/work-items";

    [Test]
    [Arguments(false, false, true)]
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    [Arguments(true, false, false)]
    public void Validator_AcceptsEveryCombinationExceptWorkflowsOnWithoutWorkSessions(bool devWorkflows, bool workSessions, bool expected)
    {
        var validator = new DevWorkflowOptionsValidator(Options.Create(new WorkSessionOptions
        {
            Enabled = workSessions
        }));

        var result = validator.Validate(name: null,
            new DevWorkflowOptions
            {
                Enabled = devWorkflows
            });

        AssertEx.Equal(expected, result.Succeeded, result.FailureMessage ?? "accepted");
    }

    /// <summary>
    ///     A disabled node answers 404 rather than 500 or 403 — and the 403 is what makes this an assertion about the
    ///     middleware rather than about routing. The enabled case's Origin is deliberately hostile, so the local-API
    ///     guard behind the feature gate rejects it; getting a 404 instead proves the gate ran first and short-circuited.
    /// </summary>
    [Test]
    public async Task DevWorkflowRoute_WhenTheFeatureIsDisabled_Answers404AheadOfTheLocalApiGuard()
    {
        await using var disabled = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DevWorkflows:Enabled"] = "false"
            }
        };

        AssertEx.Equal(HttpStatusCode.NotFound, await ProbeAsync(disabled).ConfigureAwait(false), "a disabled node must not reach anything behind the gate.");

        await using var enabled = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["DevWorkflows:Enabled"] = "true",
                ["WorkSessions:Enabled"] = "true"
            }
        };

        AssertEx.Equal(HttpStatusCode.Forbidden, await ProbeAsync(enabled).ConfigureAwait(false), "with the feature on, the request reaches the local-API guard.");
    }

    private static async Task<HttpStatusCode> ProbeAsync(TestServerWebAppFactory factory)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ProbeRoute);
        request.Headers.Add("Origin", "https://elsewhere.example");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        return response.StatusCode;
    }
}
