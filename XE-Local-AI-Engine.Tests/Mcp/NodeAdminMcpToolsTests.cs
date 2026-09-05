namespace XE_Local_AI_Engine.Tests.Mcp;

using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1;
using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.Mcp.Server;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;
using ApplicationGenerationProvenance = XE_Local_AI_Engine.Client.Services.Drafting.GenerationProvenance;
using DevWorkflowNodeRunStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevWorkflowNodeRunStatus;
using DevWorkflowNodeType = XE_Local_AI_Engine.Client.Persistence.Entities.DevWorkflowNodeType;
using DevWorkflowRunStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevWorkflowRunStatus;
using DevWorkflowWorkItemStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevWorkflowWorkItemStatus;

public sealed class NodeAdminMcpToolsTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "cancel_model_pull",
        "create_agent",
        "delete_agent",
        "delete_model",
        "get_agent",
        "get_model_pull",
        "get_node_settings",
        "get_runtime_acquisition",
        "get_runtime_status",
        "get_status",
        "get_workflow_run",
        "list_workflow_runs",
        "set_default_model",
        "start_model_pull",
        "start_runtime_acquisition",
        "update_agent",
        "update_node_settings"
    ];

    private static readonly string[] ExpectedSettingsParameters =
    [
        "auto_effort_fast_model_name",
        "chat_cache_reuse",
        "default_model_name",
        "enable_tools",
        "hugging_face_default_quant",
        "keep_model_warm_enabled",
        "keep_model_warm_interval_seconds",
        "keep_model_warm_model_name",
        "kv_cache_type",
        "llama_idle_time_to_live_seconds",
        "llama_max_loaded_processes",
        "max_message_request_timeout_seconds",
        "reranker_model_name",
        "speculative_draft_gpu_layers",
        "speculative_draft_max_tokens",
        "speculative_draft_model_name",
        "speculative_mode",
        "tool_capable_models"
    ];

    [Test]
    public void ToolSurface_AdvertisesExactlyTheAgenticAdministrationToolsAndPolicy()
    {
        var names = ToolMethods().Select(static item => item.Attribute!.Name ?? item.Method.Name)
                                 .OrderBy(static name => name, StringComparer.Ordinal)
                                 .ToArray();
        var authorization = AssertEx.NotNull(typeof(NodeAdminMcpTools).GetCustomAttribute<AuthorizeAttribute>());

        AssertEx.Equal(string.Join('|', ExpectedToolNames), string.Join('|', names));
        AssertEx.Equal(NodeAuthorizationPolicies.McpAgentic, authorization.Policy!);
    }

    [Test]
    public void UpdateNodeSettings_ExposesOnlyTheExactSeventeenFieldWhitelist()
    {
        var method = AssertEx.NotNull(typeof(NodeAdminMcpTools).GetMethod(nameof(NodeAdminMcpTools.UpdateNodeSettingsAsync)));
        var names = method.GetParameters()
                          .Where(static parameter => parameter.ParameterType != typeof(CancellationToken))
                          .Select(static parameter => parameter.Name!)
                          .OrderBy(static name => name, StringComparer.Ordinal)
                          .ToArray();

        // The tool DESCRIPTION states the field count, and an agent reads that description as the contract. It had
        // already drifted once — a seventeenth field was whitelisted while the sentence still promised sixteen — so
        // the number is DERIVED from the parameter list here rather than restated, and the next field added fails this
        // test until the sentence is corrected too.
        var description = AssertEx.NotNull(method.GetCustomAttribute<DescriptionAttribute>()).Description;
        AssertEx.True(description.Contains($"{names.Length}-field", StringComparison.Ordinal),
            $"update_node_settings advertises \"{description}\" but exposes {names.Length} fields.");

        AssertEx.Equal(string.Join('|', ExpectedSettingsParameters), string.Join('|', names));
        AssertEx.False(names.Any(static name => name.Contains("custom", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("ollama", StringComparison.OrdinalIgnoreCase)
                                                || name.Contains("approval_policy", StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void OptionalMcpParameters_AllCarryExplicitDefaultsAndSchemasRemainBounded()
    {
        foreach (var method in ToolMethods().Select(static item => item.Method))
        {
            foreach (var parameter in method.GetParameters().Where(static parameter => parameter.ParameterType != typeof(CancellationToken)))
            {
                if (parameter.IsOptional)
                {
                    AssertEx.True(parameter.HasDefaultValue, $"{method.Name}.{parameter.Name} must have a C# default.");
                }
            }

            var harness = new Harness();
            var protocolTool = McpServerTool.Create(method, harness.Tools, new McpServerToolCreateOptions()).ProtocolTool;
            var schema = JsonSerializer.Serialize(protocolTool.InputSchema);
            AssertEx.False(schema.Contains("CustomToolsEnabled", StringComparison.OrdinalIgnoreCase));
            AssertEx.False(schema.Contains("OllamaEndpoint", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Test]
    public async Task ReadTools_ReturnSanitizedApplicationViews()
    {
        var harness = new Harness();
        harness.Settings.GetAgenticViewAsync(Arg.Any<CancellationToken>()).Returns(SettingsView("model-a"));
        harness.Runtime.GetStatusAsync(false, Arg.Any<CancellationToken>()).Returns(new LlamaCppRuntimeStatus(
            new LlamaCppInstalledRuntimeView("b7000", "runtime.zip", "cuda", 10, true, "/private/runtime", "secret-commit", 1, "requested", 2),
            "b7001",
            "b7002",
            true,
            false,
            2));
        harness.Runtime.GetAcquisitionStatus().Returns(new LlamaCppRuntimeAcquisitionStatus(3,
            "downloading",
            "cuda",
            "b7001",
            10,
            20,
            1,
            3,
            "sanitized failure"));

        var node = await harness.Tools.GetStatusAsync(CancellationToken.None);
        var runtime = await harness.Tools.GetRuntimeStatusAsync(CancellationToken.None);
        var acquisition = await harness.Tools.GetRuntimeAcquisition();
        var json = JsonSerializer.Serialize(new
        {
            node,
            runtime,
            acquisition
        });

        AssertEx.Equal("model-a", node.DefaultModelName!);
        AssertEx.Equal(2, node.LoadedProcessCount);
        AssertEx.Equal("b7000", runtime.InstalledTag!);
        AssertEx.Equal("sanitized failure", acquisition.SanitizedError!);
        AssertEx.False(json.Contains("/private/runtime", StringComparison.Ordinal));
        AssertEx.False(json.Contains("secret-commit", StringComparison.Ordinal));
        AssertEx.False(json.Contains("runtime.zip", StringComparison.Ordinal));
    }

    [Test]
    public async Task RuntimeAndModelMutationTools_MapSuccessBusyValidationAndNotFoundResults()
    {
        var harness = new Harness();
        var operationId = Guid.NewGuid();
        harness.Runtime.StartAcquisitionAsync(null, Arg.Any<CancellationToken>()).Returns(new LlamaCppRuntimeAcquisitionStartResult(true, "cuda", LlamaCppRuntimeAdministrationFailure.None, null));
        harness.Download.StartAsync(Arg.Any<GgufModelRequest>(), Arg.Any<CancellationToken>()).Returns(new GgufDownloadTicket("repo/model:Q4_K_M", false, operationId));
        harness.Download.GetStatus("repo/model:Q4_K_M").Returns(new GgufDownloadStatus("repo/model:Q4_K_M",
            GgufDownloadPhase.Running,
            10,
            20,
            null,
            operationId));
        harness.Download.Cancel("repo/model:Q4_K_M").Returns(true);
        harness.Models.DeleteAsync("repo/model:Q4_K_M", Arg.Any<CancellationToken>()).Returns(new LocalModelDeletionResult(true, "repo/model:Q4_K_M", true));
        harness.Models.SelectDefaultAsync("repo/model:Q4_K_M", LocalModelSelectionPolicy.InstalledLocalOnly, Arg.Any<CancellationToken>())
               .Returns(new LocalModelSelectionResult(true, "repo/model:Q4_K_M", "old"));

        AssertEx.Equal("accepted", (await harness.Tools.StartRuntimeAcquisitionAsync(CancellationToken.None)).Status);
        AssertEx.Equal(McpAdminToolFailureCodes.InvalidVariant,
            (await harness.Tools.StartRuntimeAcquisitionAsync(CancellationToken.None, "metal")).FailureCode!);
        var started = await harness.Tools.StartModelPullAsync("repo/model", CancellationToken.None, quant: "Q4_K_M");
        AssertEx.Equal("accepted", started.Status);
        AssertEx.Equal(operationId.ToString("D"), started.OperationId!);
        AssertEx.Equal("running", (await harness.Tools.GetModelPull("repo/model:Q4_K_M")).Phase!);
        AssertEx.Equal(McpAdminToolFailureCodes.ModelPullNotFound, (await harness.Tools.GetModelPull("missing")).FailureCode!);
        AssertEx.True((await harness.Tools.CancelModelPull("repo/model:Q4_K_M")).Cancelled);
        AssertEx.True((await harness.Tools.DeleteModelAsync("repo/model:Q4_K_M", CancellationToken.None)).Deleted);
        var selected = await harness.Tools.SetDefaultModelAsync("repo/model:Q4_K_M", CancellationToken.None);
        AssertEx.True(selected.Updated);
        AssertEx.Equal("old", selected.PreviousDefault!);
    }

    [Test]
    public async Task MutationTools_MapApplicationRejectionsToStableFailureCodes()
    {
        var harness = new Harness();
        harness.Runtime.StartAcquisitionAsync(null, Arg.Any<CancellationToken>()).Returns(new LlamaCppRuntimeAcquisitionStartResult(false,
            "cuda",
            LlamaCppRuntimeAdministrationFailure.Busy,
            "Runtime busy."));
        harness.Download.StartAsync(Arg.Any<GgufModelRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GgufDownloadTicket>>(_ => throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                   "Model source not found."));
        harness.Models.DeleteAsync("bad", Arg.Any<CancellationToken>()).Returns(
            new LocalModelDeletionResult(false, null, false, LocalModelAdministrationFailureCodes.InvalidModelName, "Invalid model."));
        harness.Models.SelectDefaultAsync("missing", LocalModelSelectionPolicy.InstalledLocalOnly, Arg.Any<CancellationToken>()).Returns(
            new LocalModelSelectionResult(false, null, null, LocalModelAdministrationFailureCodes.ModelNotInstalled, "Model not installed."));
        harness.Settings.ApplyAgenticPatchAsync(Arg.Any<NodeSettingsAgenticPatch>(), Arg.Any<CancellationToken>()).Returns(NodeSettingsAdministrationResult.Rejected(new StoredNodeSettings(),
        [
            new NodeSettingsValidationError(NodeSettingsField.LlamaMaxLoadedProcesses, "Invalid process count.")
        ]));

        AssertEx.Equal(McpAdminToolFailureCodes.Busy,
            (await harness.Tools.StartRuntimeAcquisitionAsync(CancellationToken.None)).FailureCode!);
        AssertEx.Equal("model_source_not_found",
            (await harness.Tools.StartModelPullAsync("missing/repo", CancellationToken.None)).FailureCode!);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.InvalidModelName,
            (await harness.Tools.DeleteModelAsync("bad", CancellationToken.None)).FailureCode!);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.ModelNotInstalled,
            (await harness.Tools.SetDefaultModelAsync("missing", CancellationToken.None)).FailureCode!);
        var settings = await harness.Tools.UpdateNodeSettingsAsync(CancellationToken.None, llama_max_loaded_processes: 0);
        AssertEx.Equal("invalid_field:llama_max_loaded_processes", settings.FailureCode!);
        AssertEx.Equal("llama_max_loaded_processes", settings.RejectedFields[0]);
    }

    [Test]
    [Arguments("Network", "model_download_network_error")]
    [Arguments("Gated", "model_source_unauthorized")]
    [Arguments("Unauthorized", "model_source_unauthorized")]
    [Arguments("DiskFull", "insufficient_disk_space")]
    [Arguments("InsufficientStorage", "insufficient_disk_space")]
    [Arguments("HashMismatch", "model_hash_mismatch")]
    [Arguments("NotFound", "model_source_not_found")]
    [Arguments("DestinationConflict", "model_pull_conflict")]
    [Arguments("ModelConflict", "model_pull_conflict")]
    [Arguments("DownloadCompensationFailed", "model_pull_compensation_failed")]
    [Arguments("DownloadFailed", "model_pull_failed")]
    public async Task GetModelPull_MapsEveryTerminalCoordinatorFailureToStableWireCode(string internalCode, string expectedCode)
    {
        var harness = new Harness();
        harness.Download.GetStatus("model").Returns(new GgufDownloadStatus("model",
            GgufDownloadPhase.Failed,
            1,
            2,
            "safe failure",
            ErrorCode: internalCode));

        var result = await harness.Tools.GetModelPull("model");

        AssertEx.Equal("failed", result.Phase!);
        AssertEx.Equal(expectedCode, result.FailureCode!);
        AssertEx.Equal("safe failure", result.DisplayMessage!);
        AssertEx.False(result.FailureCode!.Any(char.IsUpper));
    }

    [Test]
    [Arguments(HuggingFaceDownloadFailure.Network, "model_download_network_error")]
    [Arguments(HuggingFaceDownloadFailure.Gated, "model_source_unauthorized")]
    [Arguments(HuggingFaceDownloadFailure.Unauthorized, "model_source_unauthorized")]
    [Arguments(HuggingFaceDownloadFailure.DiskFull, "insufficient_disk_space")]
    [Arguments(HuggingFaceDownloadFailure.HashMismatch, "model_hash_mismatch")]
    [Arguments(HuggingFaceDownloadFailure.NotFound, "model_source_not_found")]
    [Arguments(HuggingFaceDownloadFailure.DestinationConflict, "model_pull_conflict")]
    public async Task StartModelPull_UsesTheSameStableFailureVocabularyAsTerminalPolling(HuggingFaceDownloadFailure failure,
        string expectedCode)
    {
        var harness = new Harness();
        harness.Download.StartAsync(Arg.Any<GgufModelRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<GgufDownloadTicket>>(_ => throw new HuggingFaceDownloadException(failure, "safe failure"));

        var result = await harness.Tools.StartModelPullAsync("repo/model", CancellationToken.None);

        AssertEx.Equal(expectedCode, result.FailureCode!);
        AssertEx.Equal(McpAdminWireNames.DownloadErrorCode(failure), result.FailureCode!);
        AssertEx.Equal("safe failure", result.DisplayMessage!);
    }

    [Test]
    public void AgenticSettings_AllSeventeenPropertiesHaveStableSnakeCaseWireNames()
    {
        var mapped = typeof(NodeSettingsAgenticPatch).GetProperties()
                                                     .Select(static property => McpAdminWireNames.SettingsArgument(property.Name))
                                                     .OrderBy(static name => name, StringComparer.Ordinal)
                                                     .ToArray();

        AssertEx.Equal(string.Join('|', ExpectedSettingsParameters), string.Join('|', mapped));
        AssertEx.False(mapped.Any(static name => name.Any(char.IsUpper)));
    }

    [Test]
    public async Task UpdateNodeSettings_WhenTheSaveConflicts_ReportsAConflictRatherThanAFieldRejection()
    {
        // A conflict names no field and nothing the agent sent was wrong, so mapping it onto the validation failure
        // code told a tool-using agent to "correct" a field that was never the problem instead of retrying.
        var harness = new Harness();
        harness.Settings.ApplyAgenticPatchAsync(Arg.Any<NodeSettingsAgenticPatch>(), Arg.Any<CancellationToken>())
               .Returns(NodeSettingsAdministrationResult.Conflict(new StoredNodeSettings()));

        var response = await harness.Tools.UpdateNodeSettingsAsync(CancellationToken.None, chat_cache_reuse: 512);

        AssertEx.False(response.Updated, "and the audit failure signal stays !Updated.");
        AssertEx.Equal(McpAdminToolFailureCodes.SettingsConflict, response.FailureCode!);
        AssertEx.Equal(expected: 0, response.RejectedFields.Count);
        AssertEx.True(AssertEx.NotNull(response.DisplayMessage).Contains("retry", StringComparison.Ordinal));
    }

    [Test]
    public async Task SettingsAndAgentCrud_ForwardExactApplicationContractsAndMapFailures()
    {
        var harness = new Harness();
        NodeSettingsAgenticPatch? capturedPatch = null;
        harness.Settings.GetAgenticViewAsync(Arg.Any<CancellationToken>()).Returns(SettingsView("current"));
        harness.Settings.ApplyAgenticPatchAsync(Arg.Do<NodeSettingsAgenticPatch>(patch => capturedPatch = patch), Arg.Any<CancellationToken>())
               .Returns(new NodeSettingsAdministrationResult(true, new StoredNodeSettings(), []));
        var record = AgentRecord();
        harness.Agents.CreateAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.GetByKeyAsync("agent", Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.UpdateAsync(record.Id, Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record with
        {
            Version = 2
        });
        harness.Agents.DeleteAsync(record.Id, Arg.Any<CancellationToken>()).Returns(true);

        AssertEx.Equal("current", (await harness.Tools.GetNodeSettingsAsync(CancellationToken.None)).DefaultModelName!);
        var updatedSettings = await harness.Tools.UpdateNodeSettingsAsync(CancellationToken.None,
            default_model_name: "next",
            enable_tools: true,
            tool_capable_models: ["next"]);
        var patch = AssertEx.NotNull(capturedPatch);
        AssertEx.True(updatedSettings.Updated);
        AssertEx.Equal("next", patch.DefaultModelName!);
        AssertEx.True(patch.EnableTools!.Value);

        var created = await harness.Tools.CreateAgentAsync("Agent", "Instructions", CancellationToken.None);
        AssertEx.Equal("created", created.Status);
        AssertEx.Equal(record.Id.ToString("D"), AssertEx.NotNull(created.Agent).Id);
        AssertEx.Equal("ok", (await harness.Tools.GetAgentAsync("agent", CancellationToken.None)).Status);
        AssertEx.Equal("updated", (await harness.Tools.UpdateAgentAsync("agent", "Agent", "Instructions", CancellationToken.None)).Status);
        AssertEx.True((await harness.Tools.DeleteAgentAsync("agent", CancellationToken.None)).Deleted);
        AssertEx.Equal(McpAdminToolFailureCodes.AgentNotFound, (await harness.Tools.GetAgentAsync("missing", CancellationToken.None)).FailureCode!);
        AssertEx.Equal(McpAdminToolFailureCodes.AgentNotFound,
            (await harness.Tools.UpdateAgentAsync("missing", "Agent", "Instructions", CancellationToken.None)).FailureCode!);
        AssertEx.Equal(McpAdminToolFailureCodes.AgentNotFound,
            (await harness.Tools.DeleteAgentAsync("missing", CancellationToken.None)).FailureCode!);

        harness.Agents.CreateAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>())
               .Returns<Task<AgentDefinitionRecord>>(_ => throw new AgentDefinitionValidationException("Name is required."));
        var rejected = await harness.Tools.CreateAgentAsync("", "Instructions", CancellationToken.None);
        AssertEx.Equal(McpAdminToolFailureCodes.ValidationFailed, rejected.FailureCode!);
        AssertEx.Equal("Name is required.", rejected.DisplayMessage!);
    }

    [Test]
    public async Task AgentGenerationMetadata_MatchesHttpPersistenceAndProjectsBoundedResponse()
    {
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        var harness = new Harness(time);
        AgentDefinitionInput? captured = null;
        harness.Agents.CreateAsync(Arg.Do<AgentDefinitionInput>(input => captured = input), Arg.Any<CancellationToken>())
               .Returns(call => AgentRecord() with
               {
                   GenerationMetadataJson = call.Arg<AgentDefinitionInput>().GenerationMetadataJson
               });
        var contentHash = DraftContentHash.Compute("Agent", "Description", "Instructions");
        var mcpMetadata = new McpGenerationMetadataInput
        {
            Model = "model-a",
            Mode = "improve",
            UserBrief = "brief",
            Rationale = "rationale",
            Assumptions = ["assumption"],
            Confidence = 0.8,
            GeneratedAtUtc = 123,
            DraftContentHash = contentHash
        };
        var httpInput = new CreateAgentDefinitionRequest
        {
            Name = "Agent",
            Description = "Description",
            Instructions = "Instructions",
            GenerationMetadata = new GenerationMetadata
            {
                Model = "model-a",
                Mode = DraftMode.Improve,
                UserBrief = "brief",
                Rationale = "rationale",
                Assumptions = ["assumption"],
                Confidence = 0.8,
                GeneratedAtUtc = 123,
                DraftContentHash = contentHash
            }
        }.ToInput(now);

        var result = await harness.Tools.CreateAgentAsync("Agent",
            "Instructions",
            CancellationToken.None,
            description: "Description",
            generation_metadata: mcpMetadata);

        var actual = AssertEx.NotNull(captured);
        AssertEx.Equal(httpInput.GenerationMetadataJson!, actual.GenerationMetadataJson!);
        var output = AssertEx.NotNull(AssertEx.NotNull(result.Agent).GenerationMetadata);
        AssertEx.Equal("improve", output.Mode);
        AssertEx.Equal(now.ToUnixTimeMilliseconds(), output.AcceptedAtUtc);
        AssertEx.False(output.WasEdited);
    }

    [Test]
    public async Task AgentGenerationMetadata_InvalidInputMatchesHttpValidationAndUpdateOmissionPreservesSetIfPresentContract()
    {
        var harness = new Harness();
        AgentDefinitionInput? capturedUpdate = null;
        var record = AgentRecord() with
        {
            GenerationMetadataJson = "{\"persisted\":true}"
        };
        harness.Agents.GetByKeyAsync("agent", Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.UpdateAsync(record.Id,
                   Arg.Do<AgentDefinitionInput>(input => capturedUpdate = input),
                   Arg.Any<CancellationToken>())
               .Returns(record with
               {
                   Version = 2
               });
        var tooLong = new string('x', ApplicationGenerationProvenance.MaxModelLength + 1);
        var httpError = ApplicationGenerationProvenance.Validate(new GenerationMetadataInput(tooLong,
            DraftMode.Create,
            null,
            null,
            null,
            0.5,
            1,
            null));

        var rejected = await harness.Tools.CreateAgentAsync("Agent",
            "Instructions",
            CancellationToken.None,
            generation_metadata: new McpGenerationMetadataInput
            {
                Model = tooLong,
                Confidence = 0.5
            });
        var invalidMode = await harness.Tools.CreateAgentAsync("Agent",
            "Instructions",
            CancellationToken.None,
            generation_metadata: new McpGenerationMetadataInput
            {
                Mode = "unknown",
                Confidence = 0.5
            });
        var updated = await harness.Tools.UpdateAgentAsync("agent", "Agent", "Instructions", CancellationToken.None);

        AssertEx.Equal(McpAdminToolFailureCodes.ValidationFailed, rejected.FailureCode!);
        AssertEx.Equal(httpError!, rejected.DisplayMessage!);
        AssertEx.Equal(McpAdminToolFailureCodes.ValidationFailed, invalidMode.FailureCode!);
        AssertEx.Equal("Generation metadata mode must be create or improve.", invalidMode.DisplayMessage!);
        AssertEx.Equal("updated", updated.Status);
        AssertEx.Null(AssertEx.NotNull(capturedUpdate).GenerationMetadataJson);
    }

    [Test]
    public async Task AgentCrud_UsesSharedValidationForEmptyNameOrphanApprovalsAndTopology()
    {
        var store = Substitute.For<IAgentDefinitionStore>();
        var offers = Substitute.For<ILocalToolOfferProvider>();
        offers.GetKnownToolNamesAsync(Arg.Any<CancellationToken>()).Returns(["tool-a"]);
        var service = new AgentDefinitionService(store,
            offers,
            NullLogger<AgentDefinitionService>.Instance);
        var harness = new Harness(agents: service);

        var empty = await harness.Tools.CreateAgentAsync("", "Instructions", CancellationToken.None);
        var orphan = await harness.Tools.CreateAgentAsync("Agent",
            "Instructions",
            CancellationToken.None,
            allowed_tool_names: ["tool-a"],
            tool_approvals: new Dictionary<string, bool>
            {
                ["tool-b"] = true
            });
        var topology = await harness.Tools.CreateAgentAsync("Agent",
            "Instructions",
            CancellationToken.None,
            orchestration_topology_json: "{}",
            kind: "single");
        var now = DateTimeOffset.UnixEpoch;
        var httpEmpty = await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(new CreateAgentDefinitionRequest
        {
            Name = "",
            Instructions = "Instructions"
        }.ToInput(now)));
        var httpOrphan = await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(new CreateAgentDefinitionRequest
        {
            Name = "Agent",
            Instructions = "Instructions",
            AllowedToolNames = ["tool-a"],
            ToolApprovals = new Dictionary<string, bool>
            {
                ["tool-b"] = true
            }
        }.ToInput(now)));
        var httpTopology = await AssertEx.ThrowsAsync<AgentDefinitionValidationException>(() => service.CreateAsync(new CreateAgentDefinitionRequest
        {
            Name = "Agent",
            Instructions = "Instructions",
            Kind = AgentDefinitionKind.Single,
            OrchestrationTopologyJson = "{}"
        }.ToInput(now)));

        AssertEx.Equal(httpEmpty.Message, empty.DisplayMessage!);
        AssertEx.Equal(httpOrphan.Message, orphan.DisplayMessage!);
        AssertEx.Equal(httpTopology.Message, topology.DisplayMessage!);
        AssertEx.Equal(McpAdminToolFailureCodes.ValidationFailed, orphan.FailureCode!);
        AssertEx.Equal(McpAdminToolFailureCodes.ValidationFailed, topology.FailureCode!);
    }

    [Test]
    public async Task AgentUpdateAndDelete_RacesReturnNotFoundAndSuccessfulUpdateReturnsNewVersion()
    {
        var harness = new Harness();
        var record = AgentRecord();
        harness.Agents.GetByKeyAsync("agent", Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.UpdateAsync(record.Id, Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        harness.Agents.DeleteAsync(record.Id, Arg.Any<CancellationToken>()).Returns(false);

        var updateRace = await harness.Tools.UpdateAgentAsync("agent", "Agent", "Instructions", CancellationToken.None);
        var deleteRace = await harness.Tools.DeleteAgentAsync("agent", CancellationToken.None);

        AssertEx.Equal(McpAdminToolFailureCodes.AgentNotFound, updateRace.FailureCode!);
        AssertEx.Equal(McpAdminToolFailureCodes.AgentNotFound, deleteRace.FailureCode!);

        harness.Agents.UpdateAsync(record.Id, Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record with
        {
            Version = 7
        });
        var updated = await harness.Tools.UpdateAgentAsync("agent", "Agent", "Instructions", CancellationToken.None);
        AssertEx.Equal(7, AssertEx.NotNull(updated.Agent).Version);
    }

    [Test]
    public async Task EveryAdminTool_RoutesThroughOneAuditedBoundaryExactlyOnce()
    {
        var harness = new Harness();
        var record = AgentRecord();
        harness.Settings.GetAgenticViewAsync(Arg.Any<CancellationToken>()).Returns(SettingsView("model"));
        harness.Settings.ApplyAgenticPatchAsync(Arg.Any<NodeSettingsAgenticPatch>(), Arg.Any<CancellationToken>())
               .Returns(new NodeSettingsAdministrationResult(true, new StoredNodeSettings(), []));
        harness.Runtime.GetStatusAsync(false, Arg.Any<CancellationToken>()).Returns(new LlamaCppRuntimeStatus(Installed: null,
            RecommendedTag: "b1",
            UpstreamLatestTag: null,
            UpdateAvailable: false,
            IsOffline: false,
            RunningProcessCount: 0));
        harness.Runtime.StartAcquisitionAsync(null, Arg.Any<CancellationToken>()).Returns(new LlamaCppRuntimeAcquisitionStartResult(true, "cpu", LlamaCppRuntimeAdministrationFailure.None, null));
        harness.Runtime.GetAcquisitionStatus().Returns(new LlamaCppRuntimeAcquisitionStatus(1,
            "idle",
            null,
            null,
            null,
            null,
            0,
            0,
            null));
        harness.Download.StartAsync(Arg.Any<GgufModelRequest>(), Arg.Any<CancellationToken>()).Returns(new GgufDownloadTicket("repo/model:Q4_K_M", false, Guid.NewGuid()));
        harness.Download.GetStatus("repo/model:Q4_K_M").Returns(new GgufDownloadStatus("repo/model:Q4_K_M",
            GgufDownloadPhase.Running,
            1,
            2,
            null));
        harness.Download.Cancel("repo/model:Q4_K_M").Returns(true);
        harness.Models.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new LocalModelDeletionResult(true, "repo/model:Q4_K_M", true));
        harness.Models.SelectDefaultAsync(Arg.Any<string>(), LocalModelSelectionPolicy.InstalledLocalOnly, Arg.Any<CancellationToken>())
               .Returns(new LocalModelSelectionResult(true, "repo/model:Q4_K_M", "old"));
        harness.Agents.CreateAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.GetByKeyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.UpdateAsync(record.Id, Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.DeleteAsync(record.Id, Arg.Any<CancellationToken>()).Returns(true);
        var runDetail = RunDetail();
        harness.WorkflowStore.ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<DevWorkflowWorkItemSnapshot>());
        harness.WorkflowStore.ListDefinitionsAsync(true, Arg.Any<CancellationToken>()).Returns(Array.Empty<DevWorkflowDefinitionSummary>());
        harness.WorkflowRuns.GetAsync(runDetail.Run.Id, Arg.Any<CancellationToken>()).Returns(runDetail);

        _ = await harness.Tools.GetStatusAsync(CancellationToken.None);
        _ = await harness.Tools.GetRuntimeStatusAsync(CancellationToken.None);
        _ = await harness.Tools.StartRuntimeAcquisitionAsync(CancellationToken.None);
        _ = await harness.Tools.GetRuntimeAcquisition();
        _ = await harness.Tools.StartModelPullAsync("repo/model", CancellationToken.None);
        _ = await harness.Tools.GetModelPull("repo/model:Q4_K_M");
        _ = await harness.Tools.CancelModelPull("repo/model:Q4_K_M");
        _ = await harness.Tools.DeleteModelAsync("repo/model:Q4_K_M", CancellationToken.None);
        _ = await harness.Tools.SetDefaultModelAsync("repo/model:Q4_K_M", CancellationToken.None);
        _ = await harness.Tools.GetNodeSettingsAsync(CancellationToken.None);
        _ = await harness.Tools.UpdateNodeSettingsAsync(CancellationToken.None, enable_tools: true);
        _ = await harness.Tools.GetAgentAsync("agent", CancellationToken.None);
        _ = await harness.Tools.CreateAgentAsync("Agent", "Instructions", CancellationToken.None);
        _ = await harness.Tools.UpdateAgentAsync("agent", "Agent", "Instructions", CancellationToken.None);
        _ = await harness.Tools.DeleteAgentAsync("agent", CancellationToken.None);
        _ = await harness.Tools.ListWorkflowRunsAsync(CancellationToken.None);
        _ = await harness.Tools.GetWorkflowRunAsync(runDetail.Run.Id.ToString("D"), CancellationToken.None);

        var auditEntries = harness.Logger.Entries.Where(static entry => entry.EventId.Name == "McpAdminToolInvoked").ToArray();
        AssertEx.Equal(ExpectedToolNames.Length, auditEntries.Length);
        AssertEx.Equal(string.Join('|', ExpectedToolNames),
            string.Join('|', auditEntries.Select(static entry => (string)entry.Properties["Tool"]!)
                                         .OrderBy(static name => name, StringComparer.Ordinal)));
        AssertEx.True(auditEntries.All(static entry => string.Equals((string)entry.Properties["Outcome"]!, "success", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Audit_AttributesBoundedPrincipalAndNeverLogsRawArgumentsOrExceptionPayloads()
    {
        const string secretName = "agent-name-secret-value";
        const string secretInstructions = "instructions-secret-value";
        const string exceptionSecret = "exception-secret-value";
        var harness = new Harness(principal: AgenticPrincipal("xemcp_bounded42"));
        var record = AgentRecord();
        harness.Agents.CreateAsync(Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.GetByKeyAsync("agent-secret-id", Arg.Any<CancellationToken>()).Returns(record);
        harness.Agents.UpdateAsync(record.Id, Arg.Any<AgentDefinitionInput>(), Arg.Any<CancellationToken>()).Returns(record);

        _ = await harness.Tools.CreateAgentAsync(secretName,
            secretInstructions,
            CancellationToken.None,
            description: "description-secret-value",
            allowed_tool_names: ["tool-secret-value"],
            playbook_enabled: true,
            default_temporary_chat: true,
            memory_extraction_enabled: false,
            disable_base_scaffold: true);
        _ = await harness.Tools.UpdateAgentAsync("agent-secret-id",
            secretName,
            secretInstructions,
            CancellationToken.None,
            playbook_enabled: false,
            default_temporary_chat: false,
            memory_extraction_enabled: true,
            disable_base_scaffold: false);
        harness.Runtime.GetStatusAsync(false, Arg.Any<CancellationToken>())
               .Returns<Task<LlamaCppRuntimeStatus>>(_ => throw new IOException(exceptionSecret));
        _ = await AssertEx.ThrowsAsync<IOException>(() => harness.Tools.GetRuntimeStatusAsync(CancellationToken.None));

        AssertEx.Equal(3, harness.Logger.Entries.Count);
        AssertEx.True(harness.Logger.Entries.All(static entry =>
            string.Equals((string)entry.Properties["KeyPrefix"]!, "xemcp_bounded42", StringComparison.Ordinal)));
        var allText = string.Join('\n', harness.Logger.Entries.Select(static entry => entry.Message));
        AssertEx.False(allText.Contains(secretName, StringComparison.Ordinal));
        AssertEx.False(allText.Contains(secretInstructions, StringComparison.Ordinal));
        AssertEx.False(allText.Contains("description-secret-value", StringComparison.Ordinal));
        AssertEx.False(allText.Contains("tool-secret-value", StringComparison.Ordinal));
        AssertEx.False(allText.Contains("agent-secret-id", StringComparison.Ordinal));
        AssertEx.False(allText.Contains(exceptionSecret, StringComparison.Ordinal));
        AssertEx.True(harness.Logger.Entries.All(static entry => entry.Exception is null));
        var createSummary = (string)harness.Logger.Entries[0].Properties["ArgsSummary"]!;
        AssertEx.True(createSummary.Contains("playbook_enabled=true", StringComparison.Ordinal));
        AssertEx.True(createSummary.Contains("default_temporary_chat=true", StringComparison.Ordinal));
        AssertEx.True(createSummary.Contains("memory_extraction_enabled=false", StringComparison.Ordinal));
        AssertEx.True(createSummary.Contains("disable_base_scaffold=true", StringComparison.Ordinal));
        var updateSummary = (string)harness.Logger.Entries[1].Properties["ArgsSummary"]!;
        AssertEx.True(updateSummary.Contains("playbook_enabled=false", StringComparison.Ordinal));
        AssertEx.True(updateSummary.Contains("default_temporary_chat=false", StringComparison.Ordinal));
        AssertEx.True(updateSummary.Contains("memory_extraction_enabled=true", StringComparison.Ordinal));
        AssertEx.True(updateSummary.Contains("disable_base_scaffold=false", StringComparison.Ordinal));
        AssertEx.Equal("faulted", (string)harness.Logger.Entries[2].Properties["Outcome"]!);
    }

    [Test]
    [Arguments("Password", "password-secret")]
    [Arguments("accessToken", "token-secret")]
    [Arguments("apiKey", "key-secret")]
    [Arguments("clientSecret", "client-secret")]
    public void Audit_RedactsEverySensitiveArgumentNameBranch(string argumentName, string sensitiveValue)
    {
        var method = AssertEx.NotNull(typeof(NodeAdminMcpTools).GetMethod("SummarizeAuditValue",
            BindingFlags.Static | BindingFlags.NonPublic));

        var summary = (string)AssertEx.NotNull(method.Invoke(null, [argumentName, sensitiveValue]));

        AssertEx.Equal("[redacted]", summary);
        AssertEx.False(summary.Contains(sensitiveValue, StringComparison.Ordinal));
    }

    [Test]
    public async Task Audit_DistinguishesRejectedCancelledAndFaultedWithoutDuplicates()
    {
        var rejected = new Harness();
        _ = await rejected.Tools.StartRuntimeAcquisitionAsync(CancellationToken.None, "invalid-secret-variant");

        var cancelled = new Harness();
        cancelled.Settings.GetAgenticViewAsync(Arg.Any<CancellationToken>())
                 .Returns<Task<NodeSettingsAgenticView>>(_ => throw new OperationCanceledException("cancel-secret"));
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => cancelled.Tools.GetStatusAsync(CancellationToken.None));

        var faulted = new Harness();
        faulted.Runtime.GetStatusAsync(false, Arg.Any<CancellationToken>())
               .Returns<Task<LlamaCppRuntimeStatus>>(_ => throw new IOException("fault-secret"));
        _ = await AssertEx.ThrowsAsync<IOException>(() => faulted.Tools.GetRuntimeStatusAsync(CancellationToken.None));

        AssertSingleOutcome(rejected, "rejected");
        AssertSingleOutcome(cancelled, "cancelled");
        AssertSingleOutcome(faulted, "faulted");
        AssertEx.False(cancelled.Logger.Entries[0].Message.Contains("cancel-secret", StringComparison.Ordinal));
        AssertEx.False(faulted.Logger.Entries[0].Message.Contains("fault-secret", StringComparison.Ordinal));
    }

    [Test]
    public async Task Audit_WithoutValidAgenticProvenance_FailsClosedAndEmitsOneUnattributedRejection()
    {
        ClaimsPrincipal[] invalidPrincipals =
        [
            new ClaimsPrincipal(new ClaimsIdentity()),
            AgenticPrincipal("untrusted-prefix-secret!!")
        ];

        foreach (var principal in invalidPrincipals)
        {
            var harness = new Harness(principal: principal);

            _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.Tools.CancelModelPull("model"));

            AssertEx.Equal(0, harness.Download.ReceivedCalls().Count());
            AssertEx.Equal(1, harness.Logger.Entries.Count);
            AssertEx.Equal("unattributed", (string)harness.Logger.Entries[0].Properties["KeyPrefix"]!);
            AssertEx.Equal("rejected", (string)harness.Logger.Entries[0].Properties["Outcome"]!);
            AssertEx.False(harness.Logger.Entries[0].Message.Contains("untrusted-prefix-secret!!", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task Audit_WhenLoggerThrows_PreservesSuccessFaultAndCancellationWithoutRetryingOperations()
    {
        var successLogger = new ThrowingLogger();
        var successful = new Harness(logger: successLogger);
        successful.Models.DeleteAsync("model", Arg.Any<CancellationToken>()).Returns(new LocalModelDeletionResult(true, "model", true));

        var result = await successful.Tools.DeleteModelAsync("model", CancellationToken.None);

        AssertEx.True(result.Deleted);
        await successful.Models.Received(1).DeleteAsync("model", Arg.Any<CancellationToken>());
        AssertEx.Equal(1, successLogger.CallCount);

        var originalFault = new IOException("original-fault-secret");
        var faultLogger = new ThrowingLogger();
        var faulted = new Harness(logger: faultLogger);
        faulted.Models.DeleteAsync("model", Arg.Any<CancellationToken>())
               .Returns<Task<LocalModelDeletionResult>>(_ => throw originalFault);

        var actualFault = await AssertEx.ThrowsAsync<IOException>(() =>
            faulted.Tools.DeleteModelAsync("model", CancellationToken.None));

        AssertEx.True(ReferenceEquals(originalFault, actualFault));
        await faulted.Models.Received(1).DeleteAsync("model", Arg.Any<CancellationToken>());
        AssertEx.Equal(1, faultLogger.CallCount);

        var originalCancellation = new OperationCanceledException("original-cancellation-secret");
        var cancellationLogger = new ThrowingLogger();
        var cancelled = new Harness(logger: cancellationLogger);
        cancelled.Models.DeleteAsync("model", Arg.Any<CancellationToken>())
                 .Returns<Task<LocalModelDeletionResult>>(_ => throw originalCancellation);

        var actualCancellation = await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            cancelled.Tools.DeleteModelAsync("model", CancellationToken.None));

        AssertEx.True(ReferenceEquals(originalCancellation, actualCancellation));
        await cancelled.Models.Received(1).DeleteAsync("model", Arg.Any<CancellationToken>());
        AssertEx.Equal(1, cancellationLogger.CallCount);
    }

    [Test]
    public async Task ListWorkflowRuns_ReturnsOneBoundedRowPerWorkItemsLatestRunAndFiltersByStatus()
    {
        var harness = new Harness();
        var running = WorkItem("Ship it", DevWorkflowRunStatus.Running);
        var completed = WorkItem("Shipped", DevWorkflowRunStatus.Completed);
        var never = WorkItem("Never started", null);
        harness.WorkflowStore.ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>())
               .Returns([running, completed, never]);

        var all = await harness.Tools.ListWorkflowRunsAsync(CancellationToken.None);

        AssertEx.Equal("ok", all.Status);
        AssertEx.Equal(2, all.Count);
        AssertEx.Equal(20, all.Limit);
        AssertEx.Equal(running.LatestRunId!.Value.ToString("D"), all.Runs[0].RunId);
        AssertEx.Equal(running.Id.ToString("D"), all.Runs[0].WorkItemId);
        AssertEx.Equal("seeded", all.Runs[0].DefinitionName!);
        AssertEx.Equal("Running", all.Runs[0].Status);
        AssertEx.Equal(1, all.Runs[0].QueuedNodeCount);
        AssertEx.Equal(2, all.Runs[0].RunningNodeCount);
        AssertEx.Equal(3, all.Runs[0].CompletedNodeCount);
        AssertEx.Equal(7, all.Runs[0].TotalNodeCount);
        AssertEx.Equal(1, all.Runs[0].PendingDecisionCount);

        var filtered = await harness.Tools.ListWorkflowRunsAsync(CancellationToken.None, limit: 500, status: "completed");

        AssertEx.Equal("ok", filtered.Status);
        AssertEx.Equal(1, filtered.Count);
        AssertEx.Equal(50, filtered.Limit);
        AssertEx.Equal(completed.LatestRunId!.Value.ToString("D"), filtered.Runs[0].RunId);
    }

    [Test]
    public async Task ListWorkflowRuns_RejectsAnUnknownStatusStructurallyWithoutQueryingTheStore()
    {
        var harness = new Harness();

        var response = await harness.Tools.ListWorkflowRunsAsync(CancellationToken.None, status: "nonsense");

        AssertEx.Equal("invalid_status", response.Status);
        AssertEx.Equal("invalid_status", response.FailureCode!);
        AssertEx.Empty(response.Runs);
        await harness.WorkflowStore.DidNotReceive().ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>());
        AssertSingleOutcome(harness, "rejected");
    }

    [Test]
    public async Task GetWorkflowRun_ReturnsTheNarrowSummaryAndNodeRowsAndNothingElse()
    {
        var harness = new Harness();
        var detail = RunDetail();
        harness.WorkflowRuns.GetAsync(detail.Run.Id, Arg.Any<CancellationToken>()).Returns(detail);
        harness.WorkflowStore.ListDefinitionsAsync(true, Arg.Any<CancellationToken>())
               .Returns([
                   new DevWorkflowDefinitionSummary(detail.Run.DefinitionId,
                       "seeded",
                       "hash",
                       2,
                       DevWorkflowDefinitionSource.Seeded,
                       "slug",
                       false,
                       1,
                       10,
                       10)
               ]);

        var response = await harness.Tools.GetWorkflowRunAsync(detail.Run.Id.ToString("D"), CancellationToken.None);

        AssertEx.Equal("ok", response.Status);
        var run = AssertEx.NotNull(response.Run);
        AssertEx.Equal(detail.Run.Id.ToString("D"), run.RunId);
        AssertEx.Equal(detail.Run.WorkItemId.ToString("D"), run.WorkItemId);
        AssertEx.Equal("seeded", run.DefinitionName!);
        AssertEx.Equal("WaitingForApproval", run.Status);
        AssertEx.Equal(0, run.QueuedNodeCount);
        AssertEx.Equal(1, run.RunningNodeCount);
        AssertEx.Equal(1, run.CompletedNodeCount);
        AssertEx.Equal(2, run.TotalNodeCount);
        AssertEx.Equal(1, run.PendingDecisionCount);
        AssertEx.Equal("gate_rejected", run.FailureClass!);
        AssertEx.Equal("a sanitized reason", run.TerminalReason!);
        AssertEx.Equal(11L, run.StartedAtUtc!.Value);
        AssertEx.Equal(12L, run.EndedAtUtc!.Value);
        AssertEx.Equal(2, run.Nodes.Count);
        AssertEx.Equal("plan", run.Nodes[0].NodeKey);
        AssertEx.Equal("Agent", run.Nodes[0].NodeType);
        AssertEx.Equal("Succeeded", run.Nodes[0].Status);
        AssertEx.Equal(1, run.Nodes[0].Attempt);
        AssertEx.Equal(3, run.Nodes[0].MaxAttempts);

        // The narrow projection is the point: nothing here can carry the pinned graph, an artifact body, a transcript
        // or a host path, because there is no member on the wire shape that could hold one.
        var serialized = JsonSerializer.Serialize(response);
        AssertEx.False(serialized.Contains("graph", StringComparison.OrdinalIgnoreCase), serialized);
        AssertEx.False(serialized.Contains("secret-input", StringComparison.Ordinal), serialized);

        // FLAT on the wire too: the summary's fields sit on the run envelope itself, so a client reads run.status and
        // never run.run.status.
        using var document = JsonDocument.Parse(serialized);
        var wireRun = document.RootElement.GetProperty("Run");
        AssertEx.Equal("WaitingForApproval", wireRun.GetProperty("Status").GetString()!);
        AssertEx.Equal("gate_rejected", wireRun.GetProperty("FailureClass").GetString()!);
        AssertEx.False(wireRun.TryGetProperty("Run", out _), $"the summary must not be nested under a second run key: {serialized}");
    }

    [Test]
    public async Task GetWorkflowRun_AnswersAMissingOrMalformedRunIdStructurally()
    {
        var harness = new Harness();
        var runId = Guid.NewGuid();
        harness.WorkflowRuns.GetAsync(runId, Arg.Any<CancellationToken>())
               .Returns<Task<DevWorkflowRunDetail>>(_ => throw new DevWorkflowNotFoundException("gone"));

        var missing = await harness.Tools.GetWorkflowRunAsync(runId.ToString("D"), CancellationToken.None);
        var malformed = await new Harness().Tools.GetWorkflowRunAsync("not-a-uuid", CancellationToken.None);

        // The canonical form only, and never the all-zero id: the braced and unhyphenated spellings would each name the
        // same run under a second identity, and Guid.Empty is what an uninitialized caller sends rather than a run.
        var braced = await new Harness().Tools.GetWorkflowRunAsync(runId.ToString("B"), CancellationToken.None);
        var unhyphenated = await new Harness().Tools.GetWorkflowRunAsync(runId.ToString("N"), CancellationToken.None);
        var empty = await new Harness().Tools.GetWorkflowRunAsync(Guid.Empty.ToString("D"), CancellationToken.None);

        AssertEx.Equal("not_found", missing.Status);
        AssertEx.Equal("run_not_found", missing.FailureCode!);
        AssertEx.Null(missing.Run);
        AssertEx.Equal("invalid_request", malformed.Status);
        AssertEx.Equal("invalid_request", malformed.FailureCode!);
        AssertEx.Equal("invalid_request", braced.FailureCode!);
        AssertEx.Equal("invalid_request", unhyphenated.FailureCode!);
        AssertEx.Equal("invalid_request", empty.FailureCode!);
    }

    [Test]
    public async Task WorkflowObserveTools_AnswerNotAvailableWhenTheFeatureIsOffRatherThanFaulting()
    {
        var harness = new Harness(devWorkflowsEnabled: false);

        var list = await harness.Tools.ListWorkflowRunsAsync(CancellationToken.None);
        var get = await harness.Tools.GetWorkflowRunAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);

        AssertEx.Equal("not_available", list.Status);
        AssertEx.Equal("not_available", list.FailureCode!);
        AssertEx.Empty(list.Runs);
        AssertEx.Equal("not_available", get.Status);
        AssertEx.Equal("not_available", get.FailureCode!);
        AssertEx.Null(get.Run);
        await harness.WorkflowStore.DidNotReceive().ListWorkItemsAsync(Arg.Any<DevWorkflowWorkItemStatus?>(), Arg.Any<CancellationToken>());
        await harness.WorkflowRuns.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static DevWorkflowWorkItemSnapshot WorkItem(string title, DevWorkflowRunStatus? latestRunStatus) =>
        new(Guid.NewGuid(),
            title,
            "request",
            DevWorkflowWorkItemStatus.Active,
            null,
            latestRunStatus is null ? null : Guid.NewGuid(),
            latestRunStatus,
            latestRunStatus is null ? null : "seeded",
            latestRunStatus is null ? DevWorkflowNodeCounters.Empty : new DevWorkflowNodeCounters(1, 2, 3, 7, 1, null),
            10,
            10,
            1);

    private static DevWorkflowRunDetail RunDetail() =>
        new(new DevWorkflowRunSnapshot(Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                "hash",
                """{"schemaVersion":1,"nodes":[],"edges":[]}""",
                1,
                DevWorkflowRunStatus.WaitingForApproval,
                4,
                "gate_rejected",
                "a sanitized reason",
                11,
                12,
                10,
                10,
                1),
            [NodeRun("plan", DevWorkflowNodeRunStatus.Succeeded), NodeRun("build", DevWorkflowNodeRunStatus.Running)],
            1,
            null);

    // The switch throws on an unmapped member, and a validation error is the only thing that reaches it — so a new
    // whitelisted setting whose arm was forgotten turns an operator's rejection into a 500 on the one path that was
    // supposed to explain what they got wrong. Asked over the enum, so the next member cannot regress it either.
    [Test]
    public void SettingsField_NamesEveryNodeSettingsField()
    {
        foreach (var field in Enum.GetValues<NodeSettingsField>())
        {
            AssertEx.NotEmpty(McpAdminWireNames.SettingsField(field), $"{field} has no snake_case name on the MCP surface.");
        }
    }

    private static DevWorkflowNodeRunSnapshot NodeRun(string nodeKey, DevWorkflowNodeRunStatus status) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            nodeKey,
            DevWorkflowNodeType.Agent,
            1,
            3,
            0,
            status,
            null,
            null,
            1,
            null,
            false,
            null,
            null,
            null,
            """{"objective":"secret-input"}""",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            10);

    private static void AssertSingleOutcome(Harness harness, string outcome)
    {
        AssertEx.Equal(1, harness.Logger.Entries.Count);
        AssertEx.Equal("McpAdminToolInvoked", harness.Logger.Entries[0].EventId.Name!);
        AssertEx.Equal(outcome, (string)harness.Logger.Entries[0].Properties["Outcome"]!);
        AssertEx.True((long)harness.Logger.Entries[0].Properties["DurationMs"]! >= 0);
    }

    private static AgentDefinitionRecord AgentRecord() =>
        new(Guid.NewGuid(),
            "Agent",
            "Description",
            "Instructions",
            "model",
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(StringComparer.Ordinal),
            null,
            1,
            10,
            10);

    private static NodeSettingsAgenticView SettingsView(string model) =>
        new(model, null, null, null, null, null, null, null, null, 600, null, null, null, null, null, null, null, null);

    private static IEnumerable<(MethodInfo Method, McpServerToolAttribute? Attribute)> ToolMethods() =>
        typeof(NodeAdminMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                 .Select(static method => (Method: method,
                                     Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
                                 .Where(static item => item.Attribute is not null);

    private sealed class Harness
    {
        public Harness(TimeProvider? timeProvider = null,
            IAgentDefinitionService? agents = null,
            ClaimsPrincipal? principal = null,
            ILogger<NodeAdminMcpTools>? logger = null,
            bool devWorkflowsEnabled = true)
        {
            Agents = agents ?? Substitute.For<IAgentDefinitionService>();
            HttpContextAccessor.HttpContext = new DefaultHttpContext
            {
                User = principal ?? AgenticPrincipal()
            };
            Tools = new NodeAdminMcpTools(Runtime,
                Download,
                Models,
                Settings,
                Agents,
                WorkflowRuns,
                WorkflowStore,
                Options.Create(new DevWorkflowOptions
                {
                    Enabled = devWorkflowsEnabled
                }),
                timeProvider ?? TimeProvider.System,
                HttpContextAccessor,
                logger ?? Logger);
        }

        public IAgentDefinitionService Agents { get; }
        public IDevWorkflowRunService WorkflowRuns { get; } = Substitute.For<IDevWorkflowRunService>();
        public IDevWorkflowStore WorkflowStore { get; } = Substitute.For<IDevWorkflowStore>();
        public IGgufDownloadCoordinator Download { get; } = Substitute.For<IGgufDownloadCoordinator>();
        public ILocalModelAdministrationService Models { get; } = Substitute.For<ILocalModelAdministrationService>();
        public ILlamaCppRuntimeAdministrationService Runtime { get; } = Substitute.For<ILlamaCppRuntimeAdministrationService>();
        public INodeSettingsAdministrationService Settings { get; } = Substitute.For<INodeSettingsAdministrationService>();
        public HttpContextAccessor HttpContextAccessor { get; } = new();
        public StructuredRecordingLogger Logger { get; } = new();
        public NodeAdminMcpTools Tools { get; }
    }

    private static ClaimsPrincipal AgenticPrincipal(string keyPrefix = "xemcp_audit123") =>
        new(new ClaimsIdentity([
            new Claim(NodeAuthorizationPolicies.McpScopeClaimType, NodeAuthorizationPolicies.McpAgenticScope),
            new Claim(NodeAuthorizationPolicies.McpKeyPrefixClaimType, keyPrefix)
        ], "mcp-test"));

    private sealed class StructuredRecordingLogger : ILogger<NodeAdminMcpTools>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>>;
            Entries.Add(new LogEntry(eventId,
                formatter(state, exception),
                properties?.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                exception));
        }
    }

    private sealed class ThrowingLogger : ILogger<NodeAdminMcpTools>
    {
        public int CallCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            CallCount++;
            throw new InvalidOperationException("audit-sink-secret-failure");
        }
    }

    private sealed record LogEntry(
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Properties,
        Exception? Exception);
}
