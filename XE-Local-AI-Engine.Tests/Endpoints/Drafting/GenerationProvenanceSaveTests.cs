namespace XE_Local_AI_Engine.Tests.Endpoints.Drafting;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The save half of AI-assisted drafting: what the ordinary create/update routes do when the submitted content came
///     out of a draft. Three behaviours are pinned here because each one is a place the feature could quietly go wrong.
///     <list type="bullet">
///         <item>
///             <c>generated: true</c> forces the Imported posture — disabled, fenced, <c>sourceUri="generated"</c> —
///             on create AND on update, from any prior state and regardless of the <c>enabled</c> the client sent.
///         </item>
///         <item>
///             An omitted <c>generationMetadata</c> preserves the stored provenance instead of clearing it (the one
///             documented deviation from the full-replacement PUT contract).
///         </item>
///         <item>
///             <c>wasEdited</c> is computed server-side by rehashing what was actually submitted, and is insensitive to
///             the CRLF a browser textarea hands LF content back as.
///         </item>
///     </list>
/// </summary>
public sealed class GenerationProvenanceSaveTests
{
    private const string AgentsRoute = "/api/local/v1/agents";
    private const string SkillsRoute = "/api/local/v1/skills";

    private const string SkillBody = "# Terraform reviewer\n\nRead the plan, then flag destructive changes.";
    private const string SkillDescription = "Reviews Terraform plans before apply.";
    private const string SkillName = "terraform-reviewer";

    private static object BuildMetadata(string draftContentHash, string brief = "An assistant for reviewing Terraform plans.")
    {
        return new
        {
            model = "qwen3.5:0.8b",
            mode = "create",
            userBrief = brief,
            rationale = "Kept the body short so the operator can extend it.",
            assumptions = new[]
            {
                "The operator runs Terraform locally."
            },
            confidence = 0.8d,
            generatedAtUtc = 1_700_000_000_000L,
            draftContentHash
        };
    }

    private static async Task<JsonDocument> SendAsync(TestServerWebAppFactory factory,
        HttpClient client,
        HttpMethod method,
        string route,
        object body,
        HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, route)
        {
            Content = JsonContent.Create(body)
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(expected, response.StatusCode, payload);

        return JsonDocument.Parse(payload);
    }

    private static async Task<JsonDocument> GetAsync(TestServerWebAppFactory factory, HttpClient client, string route)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode, payload);

        return JsonDocument.Parse(payload);
    }

    [Test]
    public async Task CreateSkill_GeneratedTrue_LandsImportedAndDisabled()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                SkillsRoute,
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody,
                    generated = true,
                    generationMetadata = BuildMetadata(DraftContentHash.Compute(SkillName, SkillDescription, SkillBody))
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        var skill = created.RootElement;
        AssertEx.Equal("Imported", skill.GetProperty("origin").GetString(), "AI-drafted content lands in the Imported (fenced) posture.");
        AssertEx.False(skill.GetProperty("enabled").GetBoolean(), "A drafted skill must arrive disabled, awaiting review.");
        AssertEx.Equal("generated", skill.GetProperty("sourceUri").GetString());
        AssertEx.True(skill.GetProperty("importedAtUtc").GetInt64() > 0, "The demotion stamps when the drafted content arrived.");

        var metadata = skill.GetProperty("generationMetadata");
        AssertEx.Equal("qwen3.5:0.8b", metadata.GetProperty("model").GetString());
        AssertEx.False(metadata.GetProperty("wasEdited").GetBoolean(), "The submitted content matched the draft, so it was not edited.");
        AssertEx.True(metadata.GetProperty("acceptedAtUtc").GetInt64() > 0, "acceptedAtUtc is stamped server-side at save time.");
    }

    [Test]
    public async Task UpdateSkill_GeneratedTrue_FromLocalEnabled_DemotesToImportedDisabled()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        // Start from the state the demotion has to survive: an ordinary operator-authored skill, Local and enabled.
        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                SkillsRoute,
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        var skillId = created.RootElement.GetProperty("id").GetGuid();
        AssertEx.Equal("Local", created.RootElement.GetProperty("origin").GetString());
        AssertEx.True(created.RootElement.GetProperty("enabled").GetBoolean());

        const string ImprovedBody = "# Terraform reviewer\n\nRead the plan, flag destructive changes, then summarise the blast radius.";

        // The client asks for enabled: true. An AI improve must override it — model-revised content is no more reviewed
        // than model-written content.
        using var updated = await SendAsync(factory,
                client,
                HttpMethod.Put,
                $"{SkillsRoute}/{skillId}",
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = ImprovedBody,
                    enabled = true,
                    generated = true,
                    generationMetadata = BuildMetadata(DraftContentHash.Compute(SkillName, SkillDescription, ImprovedBody))
                },
                HttpStatusCode.OK)
            .ConfigureAwait(false);

        AssertEx.Equal("Imported", updated.RootElement.GetProperty("origin").GetString(), "An AI improve demotes a Local skill to Imported.");
        AssertEx.False(updated.RootElement.GetProperty("enabled").GetBoolean(), "The client-supplied enabled: true must be overridden.");
        AssertEx.Equal("generated", updated.RootElement.GetProperty("sourceUri").GetString());
        AssertEx.False(updated.RootElement.GetProperty("generationMetadata").GetProperty("wasEdited").GetBoolean());
    }

    [Test]
    public async Task UpdateSkill_NonAiEdit_PreservesEnabledAndStoredMetadataWhenOmitted()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                SkillsRoute,
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody,
                    generated = true,
                    generationMetadata = BuildMetadata(DraftContentHash.Compute(SkillName, SkillDescription, SkillBody),
                        "the original brief")
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        var skillId = created.RootElement.GetProperty("id").GetGuid();

        // An ordinary edit afterwards: the operator reviewed the draft and re-enables it, and the form carries no
        // provenance block. The stored provenance must survive, and the enabled echo must be honoured.
        using var updated = await SendAsync(factory,
                client,
                HttpMethod.Put,
                $"{SkillsRoute}/{skillId}",
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody + "\n\nReviewed by the operator.",
                    enabled = true
                },
                HttpStatusCode.OK)
            .ConfigureAwait(false);

        AssertEx.True(updated.RootElement.GetProperty("enabled").GetBoolean(), "An ordinary edit echoes the operator's enabled choice.");
        AssertEx.Equal("Imported", updated.RootElement.GetProperty("origin").GetString(), "Provenance stays promote-only — an edit never launders it back to Local.");

        var metadata = updated.RootElement.GetProperty("generationMetadata");
        AssertEx.Equal("the original brief", metadata.GetProperty("userBrief").GetString(), "An omitted block preserves the stored provenance instead of clearing it.");
        AssertEx.False(metadata.GetProperty("wasEdited").GetBoolean(), "wasEdited is only recomputed when a block is echoed; the stored value stands.");
    }

    [Test]
    public async Task CreateSkill_WhenContentEditedAfterDraft_MarksWasEditedTrue()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        // The hash is over what the model drafted; the operator then rewrote the body before saving.
        var draftHash = DraftContentHash.Compute(SkillName, SkillDescription, SkillBody);

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                SkillsRoute,
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody + "\n\nAlso check for drifted state.",
                    generated = true,
                    generationMetadata = BuildMetadata(draftHash)
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        AssertEx.True(created.RootElement.GetProperty("generationMetadata").GetProperty("wasEdited").GetBoolean(),
            "Content that differs from the draft must be recorded as edited.");
    }

    [Test]
    public async Task CreateSkill_WhenBrowserReturnsDraftWithCrlfLineEndings_WasEditedStaysFalse()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        // A textarea round-trips LF content back as CRLF. That is not an operator edit, and the canonical hash folds it.
        var draftHash = DraftContentHash.Compute(SkillName, SkillDescription, SkillBody);

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                SkillsRoute,
                new
                {
                    name = SkillName,
                    description = SkillDescription,
                    body = SkillBody.Replace("\n", "\r\n", StringComparison.Ordinal),
                    generated = true,
                    generationMetadata = BuildMetadata(draftHash)
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        AssertEx.False(created.RootElement.GetProperty("generationMetadata").GetProperty("wasEdited").GetBoolean(),
            "CRLF-folded content is byte-different but not an edit.");
    }

    [Test]
    public async Task CreateSkill_WhenEchoedMetadataBreachesACap_Returns400()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, SkillsRoute)
        {
            Content = JsonContent.Create(new
            {
                name = SkillName,
                description = SkillDescription,
                body = SkillBody,
                generationMetadata = new
                {
                    model = "qwen3.5:0.8b",
                    mode = "create",
                    rationale = new string('r', count: 2001),
                    confidence = 0.5d
                }
            })
        };
        factory.AddNodeBearerToken(request);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, "Every echoed provenance field is bounded at the endpoint.");
    }

    [Test]
    public async Task CreateAgent_WithGenerationMetadata_PersistsEncryptedAndSingleItemReadsCarryIt()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        const string AgentName = "Terraform Reviewer";
        const string AgentDescription = "Reviews Terraform plans before apply.";
        const string AgentInstructions = "You review Terraform plans and flag destructive changes.";
        const string BriefSentinel = "agent-provenance-sentinel-brief";

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                AgentsRoute,
                new
                {
                    name = AgentName,
                    description = AgentDescription,
                    instructions = AgentInstructions,
                    generationMetadata = BuildMetadata(DraftContentHash.Compute(AgentName, AgentDescription, AgentInstructions),
                        BriefSentinel)
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        var agentId = created.RootElement.GetProperty("id").GetGuid();
        AssertEx.Equal(BriefSentinel, created.RootElement.GetProperty("generationMetadata").GetProperty("userBrief").GetString());

        // The single-item GET carries the provenance...
        using var fetched = await GetAsync(factory, client, $"{AgentsRoute}/{agentId}").ConfigureAwait(false);
        var metadata = fetched.RootElement.GetProperty("generationMetadata");
        AssertEx.Equal(BriefSentinel, metadata.GetProperty("userBrief").GetString());
        AssertEx.Equal("Create", metadata.GetProperty("mode").GetString());
        AssertEx.False(metadata.GetProperty("wasEdited").GetBoolean());
        AssertEx.True(metadata.GetProperty("acceptedAtUtc").GetInt64() > 0);

        // ...and the list does not, so a library listing never ships a rationale and brief per row.
        using var listed = await GetAsync(factory, client, AgentsRoute).ConfigureAwait(false);
        var listedAgent = listed.RootElement.GetProperty("items")
                                .EnumerateArray()
                                .Single(item => item.GetProperty("id").GetGuid() == agentId);
        AssertEx.Equal(JsonValueKind.Null, listedAgent.GetProperty("generationMetadata").ValueKind, "List rows leave the provenance null.");

        // At rest the column is ciphertext: the brief quotes the operator, so it lives on the encrypted surface with the
        // instructions it was drafted from. Read as a raw scalar, which never passes the materialization interceptor.
        var stored = await ReadRawStoredProvenanceAsync(factory).ConfigureAwait(false);
        AssertEx.NotNull(stored, "The column should hold a payload.");
        AssertEx.False(Encoding.UTF8.GetString(stored!).Contains(BriefSentinel, StringComparison.Ordinal),
            "The stored provenance must be encrypted at rest, not readable plaintext JSON.");
    }

    [Test]
    public async Task UpdateAgent_WithGenerationMetadata_PersistsAndOmittingItPreservesTheStoredBlock()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        const string AgentName = "Terraform Reviewer";
        const string AgentInstructions = "You review Terraform plans and flag destructive changes.";

        using var created = await SendAsync(factory,
                client,
                HttpMethod.Post,
                AgentsRoute,
                new
                {
                    name = AgentName,
                    instructions = AgentInstructions
                },
                HttpStatusCode.Created)
            .ConfigureAwait(false);

        var agentId = created.RootElement.GetProperty("id").GetGuid();
        AssertEx.Equal(JsonValueKind.Null, created.RootElement.GetProperty("generationMetadata").ValueKind, "A plain create carries no provenance.");

        const string ImprovedInstructions = "You review Terraform plans, flag destructive changes, and summarise the blast radius.";

        using var improved = await SendAsync(factory,
                client,
                HttpMethod.Put,
                $"{AgentsRoute}/{agentId}",
                new
                {
                    name = AgentName,
                    instructions = ImprovedInstructions,
                    generationMetadata = BuildMetadata(DraftContentHash.Compute(AgentName, description: null, ImprovedInstructions),
                        "improve-brief")
                },
                HttpStatusCode.OK)
            .ConfigureAwait(false);

        AssertEx.Equal("improve-brief", improved.RootElement.GetProperty("generationMetadata").GetProperty("userBrief").GetString());
        AssertEx.False(improved.RootElement.GetProperty("generationMetadata").GetProperty("wasEdited").GetBoolean());

        // A later edit that carries no block must not clear what is stored — the documented deviation from PUT's
        // full-replacement contract.
        using var edited = await SendAsync(factory,
                client,
                HttpMethod.Put,
                $"{AgentsRoute}/{agentId}",
                new
                {
                    name = AgentName,
                    instructions = ImprovedInstructions + " Then stop."
                },
                HttpStatusCode.OK)
            .ConfigureAwait(false);

        AssertEx.Equal("improve-brief", edited.RootElement.GetProperty("generationMetadata").GetProperty("userBrief").GetString(),
            "An omitted provenance block preserves the stored one.");
    }

    // Reads the raw generation_metadata_json bytes straight out of SQLite — the only agent row in this test host that
    // has one. Going through the DbContext's entity materialization would hand back the DECRYPTED payload (the
    // interceptor runs on load), which is exactly what this assertion must not see.
    private static async Task<byte[]?> ReadRawStoredProvenanceAsync(TestServerWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync().ConfigureAwait(false);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT generation_metadata_json FROM agent_definitions WHERE generation_metadata_json IS NOT NULL";

            var value = await command.ExecuteScalarAsync().ConfigureAwait(false);

            return value as byte[];
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
