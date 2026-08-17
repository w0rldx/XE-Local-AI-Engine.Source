namespace XE_Local_AI_Engine.Tests.Endpoints.Drafting;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two AI-drafting endpoints (<c>agents/draft</c>, <c>skills/draft</c>). They are Operator-gated, cap every
///     input at the boundary, map the drafting service's typed failures onto 400/409/422, and — the invariant that
///     makes the whole surface safe — write nothing: only the existing CRUD routes persist.
///     <para>
///         Eligibility, the admission gate and envelope normalization are unit-tested against the real service; these
///         tests substitute a stub <see cref="IConfigDraftService" /> so they pin the endpoint contract alone. The one
///         exception is the aggregate prompt budget, which is deliberately exercised through the real service with a
///         lowered ceiling, since that check is the service's job by design.
///     </para>
/// </summary>
public sealed class DraftEndpointTests
{
    private const string AgentDraftRoute = "/api/local/v1/agents/draft";
    private const string SkillDraftRoute = "/api/local/v1/skills/draft";

    private static object BuildCreateBody(string brief = "An agent that reviews Terraform plans before apply.")
    {
        return new
        {
            mode = "create",
            modelName = "qwen3.5:0.8b",
            brief
        };
    }

    private static ConfigDraft BuildDraft()
    {
        return new ConfigDraft("terraform-reviewer",
            "Reviews Terraform plans before apply.",
            "# Terraform reviewer\n\nRead the plan, then flag destructive changes.",
            "Kept the instructions short so the operator can extend them.",
            ["The operator runs Terraform locally."],
            Confidence: 0.8d,
            DateTimeOffset.UnixEpoch.AddSeconds(1_700_000_000),
            "0123456789abcdef");
    }

    private static TestServerWebAppFactory CreateFactory(StubConfigDraftService stub)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IConfigDraftService>();
                services.AddScoped<IConfigDraftService>(_ => stub);
            }
        };
    }

    private static async Task<HttpResponseMessage> PostAsync(TestServerWebAppFactory factory,
        HttpClient client,
        string route,
        object body,
        bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(body)
        };

        if (authenticated)
        {
            factory.AddNodeBearerToken(request);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    [Test]
    public async Task DraftAgent_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody(), authenticated: false).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount, "An unauthenticated request must never reach the drafting service.");
    }

    [Test]
    public async Task DraftSkill_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, SkillDraftRoute, BuildCreateBody(), authenticated: false).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount, "An unauthenticated request must never reach the drafting service.");
    }

    [Test]
    public async Task DraftAgent_WhenModelMissing_Returns400WithoutCallingService()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory,
                client,
                AgentDraftRoute,
                new
                {
                    mode = "create",
                    brief = "An agent that reviews Terraform plans."
                })
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount, "A request missing the model must be rejected before the drafting service runs.");
    }

    [Test]
    public async Task DraftAgent_WhenBriefOverCap_Returns400WithoutCallingService()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody(new string('b', count: 4001))).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount, "An oversized brief must never occupy the single draft slot.");
    }

    [Test]
    public async Task DraftAgent_WhenExistingContentOverCap_Returns400WithoutCallingService()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory,
                client,
                AgentDraftRoute,
                new
                {
                    mode = "improve",
                    modelName = "qwen3.5:0.8b",
                    brief = "Make it stricter.",
                    existingContent = new string('c', count: 20001)
                })
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount, "Improve-mode content is capped at the endpoint like every other field.");
    }

    [Test]
    public async Task DraftAgent_WhenExistingNameOverAgentCap_Returns400()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory,
                client,
                AgentDraftRoute,
                new
                {
                    mode = "improve",
                    modelName = "qwen3.5:0.8b",
                    brief = "Make it stricter.",
                    existingName = new string('n', count: 121)
                })
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount);
    }

    [Test]
    public async Task DraftSkill_WhenExistingNameOverSkillCap_Returns400()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        // 65 characters is inside the AGENT cap (120) and outside the SKILL cap (64): the two surfaces really do carry
        // different ceilings, so a shared cap would have let this through.
        using var response = await PostAsync(factory,
                client,
                SkillDraftRoute,
                new
                {
                    mode = "improve",
                    modelName = "qwen3.5:0.8b",
                    brief = "Make it stricter.",
                    existingName = new string('n', count: 65)
                })
            .ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Equal(expected: 0, stub.CallCount);
    }

    [Test]
    public async Task DraftAgent_WhenAggregatePromptBudgetExceeded_Returns400()
    {
        // The REAL service, with the prompt budget lowered below the per-field caps so the aggregate check is reachable.
        // With production values (60000 vs. a 22120-character worst case) the per-field caps subsume it, which is the
        // point: the budget is the belt behind the endpoint's brace, and it rejects before the gate or any model work.
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = static services =>
                services.Configure<DraftingOptions>(static options => options.MaxPromptChars = 10)
        };
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody("A brief comfortably longer than ten characters.")).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task DraftAgent_WhenModelNotEligible_Returns400()
    {
        var stub = new StubConfigDraftService(DraftResult.Failed(DraftFailureKind.ModelNotEligible,
            "The selected model is not an installed chat model served by a node-local runtime."));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Test]
    public async Task DraftAgent_WhenNodeBusy_Returns409WithTypedCode()
    {
        var stub = new StubConfigDraftService(DraftResult.Failed(DraftFailureKind.NodeBusy,
            "The node is running another task; try again once it finishes."));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("NodeBusy", document.RootElement.GetProperty("code").GetString());
        AssertEx.NotNullOrEmpty(document.RootElement.GetProperty("message").GetString());
    }

    [Test]
    public async Task DraftSkill_WhenUnparseable_Returns422WithTypedCode()
    {
        var stub = new StubConfigDraftService(DraftResult.Failed(DraftFailureKind.Unparseable,
            "The model did not return a usable draft."));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        using var response = await PostAsync(factory, client, SkillDraftRoute, BuildCreateBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Equal("Unparseable", document.RootElement.GetProperty("code").GetString());
    }

    [Test]
    public async Task DraftEndpoints_WhenSuccessful_ReturnDraftAndPersistNothing()
    {
        var stub = new StubConfigDraftService(DraftResult.Success(BuildDraft()));
        await using var factory = CreateFactory(stub);
        using var client = factory.CreateClient();

        var (agentsBefore, skillsBefore) = await CountRowsAsync(factory).ConfigureAwait(false);

        using var agentResponse = await PostAsync(factory, client, AgentDraftRoute, BuildCreateBody()).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, agentResponse.StatusCode);

        using var agentDocument = JsonDocument.Parse(await agentResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        var agentDraft = agentDocument.RootElement;
        AssertEx.Equal("terraform-reviewer", agentDraft.GetProperty("name").GetString());
        AssertEx.Contains(agentDraft.GetProperty("instructions").GetString(), "Terraform reviewer");

        // The provenance block carries the request's own model/mode/brief plus what the service stamped, so the save
        // path can echo one opaque object back.
        var metadata = agentDraft.GetProperty("generationMetadata");
        AssertEx.Equal("qwen3.5:0.8b", metadata.GetProperty("model").GetString());
        AssertEx.Equal("Create", metadata.GetProperty("mode").GetString());
        AssertEx.Equal("An agent that reviews Terraform plans before apply.", metadata.GetProperty("userBrief").GetString());
        AssertEx.Equal("0123456789abcdef", metadata.GetProperty("draftContentHash").GetString());
        AssertEx.Equal(expected: 1_700_000_000_000L, metadata.GetProperty("generatedAtUtc").GetInt64());
        AssertEx.NotEmpty(metadata.GetProperty("assumptions").EnumerateArray().ToList());

        using var skillResponse = await PostAsync(factory, client, SkillDraftRoute, BuildCreateBody()).ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, skillResponse.StatusCode);

        using var skillDocument = JsonDocument.Parse(await skillResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
        AssertEx.Contains(skillDocument.RootElement.GetProperty("body").GetString(), "Terraform reviewer");

        var (agentsAfter, skillsAfter) = await CountRowsAsync(factory).ConfigureAwait(false);
        AssertEx.Equal(agentsBefore, agentsAfter, "Drafting an agent must not write a row — the CRUD routes are the only persistence path.");
        AssertEx.Equal(skillsBefore, skillsAfter, "Drafting a skill must not write a row — the CRUD routes are the only persistence path.");
        AssertEx.Equal(expected: 1, stub.AgentCallCount);
        AssertEx.Equal(expected: 1, stub.SkillCallCount);
    }

    private static async Task<(long Agents, long Skills)> CountRowsAsync(TestServerWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM agent_definitions) AS agents, (SELECT COUNT(*) FROM agent_skills) AS skills";

            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            _ = await reader.ReadAsync().ConfigureAwait(false);

            return (reader.GetInt64(ordinal: 0), reader.GetInt64(ordinal: 1));
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Returns a fixed outcome and records that it was reached. Eligibility, the gate and normalization all live in
    ///     the real service's own unit tests; here the point is what the endpoint does with each outcome.
    /// </summary>
    private sealed class StubConfigDraftService(DraftResult result) : IConfigDraftService
    {
        public int AgentCallCount { get; private set; }

        public int SkillCallCount { get; private set; }

        public int CallCount => AgentCallCount + SkillCallCount;

        public Task<DraftResult> DraftAgentDefinitionAsync(ConfigDraftRequest request, CancellationToken cancellationToken = default)
        {
            AgentCallCount++;
            return Task.FromResult(result);
        }

        public Task<DraftResult> DraftSkillAsync(ConfigDraftRequest request, CancellationToken cancellationToken = default)
        {
            SkillCallCount++;
            return Task.FromResult(result);
        }
    }
}
