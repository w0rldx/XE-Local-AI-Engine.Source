namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Configuration;
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
    ///     The node cap's own bound, probed from both sides. The data annotation IS the gate for this member — the
    ///     hand-written validator above checks the one cross-section relation and leaves every budget to its range — so
    ///     a bound quietly widened, or the attribute dropped in a refactor, shows up here rather than as an operator
    ///     configuration that starts when it should have refused.
    /// </summary>
    [Test]
    [Arguments(0, false, "a cap that admits no graph at all")]
    [Arguments(1, true, "the floor exactly")]
    [Arguments(500, true, "the documented default")]
    [Arguments(10_000, true, "the ceiling exactly")]
    [Arguments(10_001, false, "a cap above the ceiling")]
    public void MaxNodesPerDefinition_IsHeldToItsAnnotatedRange(int maxNodesPerDefinition, bool expected, string because)
    {
        var options = new DevWorkflowOptions
        {
            MaxNodesPerDefinition = maxNodesPerDefinition
        };
        var errors = new List<ValidationResult>();

        var accepted = Validator.TryValidateObject(options, new ValidationContext(options), errors, validateAllProperties: true);

        AssertEx.Equal(expected, accepted, $"{because}: {string.Join("; ", errors.Select(static error => error.ErrorMessage))}");
    }

    /// <summary>
    ///     The default asserted over the BINDER rather than the constructor, so a drift in either the literal or the
    ///     section name reds here instead of at the first save.
    /// </summary>
    [Test]
    public void Options_BindTheDocumentedNodeCap()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                // The section exists and names no budget: what comes back is what a node ships with.
                                ["DevWorkflows:Enabled"] = "true"
                            })
                            .Build();

        var options = AssertEx.NotNull(configuration.GetSection(DevWorkflowOptions.Section).Get<DevWorkflowOptions>(), "the section must bind.");

        AssertEx.Equal("DevWorkflows", DevWorkflowOptions.Section);
        AssertEx.Equal(expected: 500, options.MaxNodesPerDefinition);
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
