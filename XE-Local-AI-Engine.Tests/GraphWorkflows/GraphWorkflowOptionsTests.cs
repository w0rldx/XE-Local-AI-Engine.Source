namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Net;
using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The graph-workflow switch and its budgets: what the validator refuses at startup, what the documented defaults
///     are, and what a node answers with the feature off.
/// </summary>
public sealed class GraphWorkflowOptionsTests
{
    private const string ProbeRoute = "/api/local/v1/graph-workflows/definitions";

    /// <summary>
    ///     Every refusal the section owns, one row per rule: each of the NINE floors at floor minus one, the two
    ///     relations (a run that could not instantiate the definition it started from, and a replay window above its
    ///     ceiling), and the accepted rows that pin the other side — the documented defaults, every floor exactly at
    ///     its floor, and the replay window exactly at its ceiling.
    ///     <para>
    ///         Each refusing row is the defaults with ONE member moved, so what it pins is that member's own floor and
    ///         not some combination. The two exceptions are the rows for the relations, which are about two members by
    ///         definition, and the <c>MaxNodeRunsPerRun</c> floor, which has to lower <c>MaxNodesPerDefinition</c> with
    ///         it or the relation would refuse the row before its floor got the chance.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(200, 200, 50, 600, 262_144, 500, 4, 65_536, 200, true, "the documented defaults")]
    [Arguments(2, 2, 1, 1, 1024, 100, 1, 1024, 1, true, "every budget exactly at its floor")]
    [Arguments(200, 200, 50, 600, 262_144, 500, 4, 65_536, 1000, true, "the replay window exactly at its ceiling")]
    [Arguments(1, 200, 50, 600, 262_144, 500, 4, 65_536, 200, false, "a definition cap that admits no graph at all")]
    [Arguments(2, 1, 50, 600, 262_144, 500, 4, 65_536, 200, false, "a run cap under its own floor")]
    [Arguments(200, 200, 0, 600, 262_144, 500, 4, 65_536, 200, false, "an attempt budget that permits no attempt")]
    [Arguments(200, 200, 50, 0, 262_144, 500, 4, 65_536, 200, false, "a default node timeout of no time at all")]
    [Arguments(200, 200, 50, 600, 1023, 500, 4, 65_536, 200, false, "an output cap under a kilobyte")]
    [Arguments(200, 200, 50, 600, 262_144, 99, 4, 65_536, 200, false, "a dispatch interval under 100 ms")]
    [Arguments(200, 200, 50, 600, 262_144, 500, 0, 65_536, 200, false, "a concurrency cap that would run nothing")]
    [Arguments(200, 200, 50, 600, 262_144, 500, 4, 1023, 200, false, "a run-input cap under a kilobyte")]
    [Arguments(200, 200, 50, 600, 262_144, 500, 4, 65_536, 0, false, "a replay window that would return nothing")]
    [Arguments(200, 100, 50, 600, 262_144, 500, 4, 65_536, 200, false, "fewer node runs per run than nodes per definition")]
    [Arguments(200, 200, 50, 600, 262_144, 500, 4, 65_536, 1001, false, "a replay window above 1000")]
    public void Validator_RefusesTheCombinationsThatCannotWork(int maxNodesPerDefinition,
        int maxNodeRunsPerRun,
        int maxTotalAttempts,
        int defaultNodeTimeoutSeconds,
        int maxOutputJsonBytes,
        int dispatchIntervalMilliseconds,
        int maxConcurrentRuns,
        int maxRunInputBytes,
        int eventReplayLimit,
        bool expected,
        string because)
    {
        var validator = new GraphWorkflowOptionsValidator();

        var result = validator.Validate(name: null,
            new GraphWorkflowOptions
            {
                MaxNodesPerDefinition = maxNodesPerDefinition,
                MaxNodeRunsPerRun = maxNodeRunsPerRun,
                MaxTotalAttempts = maxTotalAttempts,
                DefaultNodeTimeoutSeconds = defaultNodeTimeoutSeconds,
                MaxOutputJsonBytes = maxOutputJsonBytes,
                DispatchIntervalMilliseconds = dispatchIntervalMilliseconds,
                MaxConcurrentRuns = maxConcurrentRuns,
                MaxRunInputBytes = maxRunInputBytes,
                EventReplayLimit = eventReplayLimit
            });

        AssertEx.Equal(expected, result.Succeeded, $"{because}: {result.FailureMessage ?? "accepted"}");
    }

    /// <summary>
    ///     Every default asserted by value, over the binder rather than the constructor, so a drift in either the
    ///     literal or the section name reds here instead of at a run.
    /// </summary>
    [Test]
    public void Options_BindTheDocumentedDefaults()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
                            {
                                // The section exists and names no budget: what comes back is what a node ships with.
                                ["GraphWorkflows:Enabled"] = "false"
                            })
                            .Build();

        var options = AssertEx.NotNull(configuration.GetSection(GraphWorkflowOptions.Section).Get<GraphWorkflowOptions>(),
            "the section must bind.");

        AssertEx.Equal("GraphWorkflows", GraphWorkflowOptions.Section);
        AssertEx.False(options.Enabled, "the feature ships off.");
        AssertEx.Equal(expected: 200, options.MaxNodesPerDefinition);
        AssertEx.Equal(expected: 200, options.MaxNodeRunsPerRun);
        AssertEx.Equal(expected: 50, options.MaxTotalAttempts);
        AssertEx.Equal(expected: 600, options.DefaultNodeTimeoutSeconds);
        AssertEx.Equal(expected: 262_144, options.MaxOutputJsonBytes);
        AssertEx.Equal(expected: 500, options.DispatchIntervalMilliseconds);
        AssertEx.Equal(expected: 4, options.MaxConcurrentRuns);
        AssertEx.Equal(expected: 65_536, options.MaxRunInputBytes);
        AssertEx.Equal(expected: 200, options.EventReplayLimit);
    }

    /// <summary>
    ///     A disabled node answers 404 rather than 500 or 403 — and the 403 is what makes this an assertion about the
    ///     middleware rather than about routing. The Origin is deliberately hostile in both halves, so with the feature
    ///     on the local-API guard rejects the request before routing ever looks for an endpoint; getting a 404 with the
    ///     feature off proves the gate ran ahead of that guard and short-circuited.
    /// </summary>
    [Test]
    public async Task GraphWorkflowRoute_WhenTheFeatureIsDisabled_Answers404AheadOfTheLocalApiGuard()
    {
        await using var disabled = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "false"
            }
        };

        AssertEx.Equal(HttpStatusCode.NotFound, await ProbeAsync(disabled).ConfigureAwait(false), "a disabled node must not reach anything behind the gate.");

        await using var enabled = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
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
