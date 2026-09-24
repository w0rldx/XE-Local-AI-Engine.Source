namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two places the <c>selectedFolderIds</c> contract exists — the model-visible schema in
///     <see cref="AgentHomeToolDefinition.ParameterSchema" /> and the authoritative
///     <c>AgentHomeRunToolRequestValidator.SelectedFolderIdPattern</c> behind the handler's compiled regex — must agree,
///     and the values a real model can emit must validate. A drift between the two is invisible until a model sends a
///     value one accepts and the other rejects, which is what the first AgentHome live round paid for. Whether the
///     shared pattern survives llama.cpp's GBNF converter is pinned separately, by
///     <c>LlamaGrammarPatternCompatibilityTests</c>.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class AgentHomeToolSchemaContractTests
{
    [Test]
    public void SchemaSelectedFolderIdPattern_IsTheSameStringTheValidatorCompiles()
    {
        using var document = JsonDocument.Parse(AgentHomeToolDefinition.ParameterSchema);

        var schemaPattern = document.RootElement
                                    .GetProperty("properties")
                                    .GetProperty("selectedFolderIds")
                                    .GetProperty("items")
                                    .GetProperty("pattern")
                                    .GetString();

        AssertEx.Equal(AgentHomeRunToolRequestValidator.SelectedFolderIdPattern, schemaPattern!);
    }

    [Test]
    [Arguments("scratch", "a plain lowercase alias")]
    [Arguments("s", "a single character is the shortest legal alias")]
    [Arguments("my-scratch-repo-2", "an alias with digits and hyphens")]
    [Arguments("05f06dd9-e81c-410b-9a1b-7ff3ae40e7da", "the workspace GUID form the operator pastes")]
    [Arguments("05F06DD9-E81C-410B-9A1B-7FF3AE40E7DA", "an uppercase GUID — the GUID alternative accepts both cases")]
    public async Task Validator_AcceptsAnAliasOrAGuid(string folderId, string because)
    {
        var handler = CreateEnabledHandler(out var gateway);

        var result = await handler.ExecuteAsync(BuildArguments(folderId));

        AssertEx.True(gateway.WasCalled, $"'{folderId}' must validate: {because}. The handler answered: {result}");
    }

    [Test]
    [Arguments("scratch$", "THE live-round regression: the trailing '$' the old GBNF grammar forced onto every value")]
    [Arguments("05f06dd9-e81c-410b-9a1b-7ff3ae40e7da$", "the live round's correct GUID plus the grammar's literal '$'")]
    [Arguments("^scratch", "a leading '^' — the other half of the same grammar defect")]
    [Arguments("Scratch", "an uppercase alias is not a legal alias")]
    [Arguments("-leading-hyphen", "an alias must start with a letter or digit")]
    [Arguments("/etc/passwd", "a raw host path")]
    [Arguments("scratch with spaces", "whitespace")]
    [Arguments("", "empty")]
    public async Task Validator_RejectsEverythingElse(string folderId, string because)
    {
        var handler = CreateEnabledHandler(out var gateway);

        var result = await handler.ExecuteAsync(BuildArguments(folderId));

        AssertEx.False(gateway.WasCalled, $"'{folderId}' must be rejected: {because}");
        AssertEx.Contains(result, "invalid id");
    }

    /// <summary>
    ///     Every <c>allowedActions</c> value the schema offers must gate something real. <c>propose_memory</c> was
    ///     REMOVED rather than left standing: the node collected the sandbox's memory proposals and then discarded
    ///     them, so advertising the value promised a capability that silently no-opped. Both halves of the contract —
    ///     the model-visible enum and the authoritative validator — have to drop it together, or a model reading the
    ///     schema will keep sending a value the handler rejects.
    /// </summary>
    [Test]
    public async Task AllowedActions_OfferExactlyTheFourThatGateSomething()
    {
        using var document = JsonDocument.Parse(AgentHomeToolDefinition.ParameterSchema);

        var offered = document.RootElement
                              .GetProperty("properties")
                              .GetProperty("allowedActions")
                              .GetProperty("items")
                              .GetProperty("enum")
                              .EnumerateArray()
                              .Select(static value => value.GetString())
                              .ToArray();

        AssertEx.Equal("read_workspace,write_workspace,run_commands,export_patch", string.Join(",", offered));

        // The validator is authoritative, so it has to agree — including about the value that was dropped.
        var errors = AgentHomeRunToolRequestValidator.Validate(new AgentHomeRunToolRequest
        {
            Goal = "g",
            SelectedFolderIds = ["scratch"],
            AllowedActions = ["propose_memory"]
        });

        AssertEx.Contains(errors, error => error.Contains("propose_memory", StringComparison.Ordinal),
            "the validator must reject a value the schema no longer offers");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Validator_BoundsTheAliasAtSixtyFourCharacters()
    {
        // The alias alternative is `[a-z0-9][a-z0-9-]{0,63}` — 64 characters at most. Pinning BOTH sides of the bound
        // is what would catch a {0,63}→{0,64} typo; asserting only the rejection would pass against a broken-shut regex.
        var atTheBound = CreateEnabledHandler(out var boundGateway);
        await atTheBound.ExecuteAsync(BuildArguments(new string('a', count: 64)));
        AssertEx.True(boundGateway.WasCalled, "a 64-character alias is exactly at the bound and must validate");

        var overTheBound = CreateEnabledHandler(out var overGateway);
        var result = await overTheBound.ExecuteAsync(BuildArguments(new string('a', count: 65)));
        AssertEx.False(overGateway.WasCalled, "a 65-character alias is over the bound");
        AssertEx.Contains(result, "invalid id");
    }

    private static string BuildArguments(string folderId)
    {
        return JsonSerializer.Serialize(new
        {
            goal = "list the files",
            selectedFolderIds = new[]
            {
                folderId
            },
            allowedActions = new[]
            {
                "read_workspace"
            }
        });
    }

    private static RunInAgentHomeToolHandler CreateEnabledHandler(out RecordingGateway gateway)
    {
        gateway = new RecordingGateway();
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["AgentHome:Enabled"] = "true"
                            })
                            .Build();

        return new RunInAgentHomeToolHandler(configuration, gateway);
    }

    private sealed class RecordingGateway : IAgentHomeToolGateway
    {
        public bool WasCalled { get; private set; }

        public Task<string> ExecuteAsync(AgentHomeRunToolRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult("run reached the gateway");
        }
    }
}
