namespace XE_Local_AI_Engine.Tests.Training;

using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the execution invariant: real execution requires <see cref="ToolCategory.ReadLocal" /> AND a composed
///     effective approval of false, and the executor's own <see cref="IToolApprovalPolicy" /> call is what decides it.
/// </summary>
public sealed class HeadlessToolExecutorTests
{
    private const string ToolName = "read_file";
    private const string Schema = """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""";
    private const string Arguments = """{"path":"README.md"}""";

    [Test]
    public async Task HeadlessExecutor_ReadLocalToolWithoutApproval_ExecutesForReal()
    {
        var harness = new Harness(ToolCategory.ReadLocal, catalogDefault: false, policyTightens: false);

        var outcome = await harness.Executor.ExecuteAsync(ToolName, Arguments, "teacher.gguf");

        AssertEx.Equal(HeadlessToolOutcomeKind.Executed, outcome.Kind);
        AssertEx.Equal("read:README.md", outcome.Result);
    }

    [Test]
    public async Task HeadlessExecutor_TightenedReadLocalTool_RoutesToMock()
    {
        var harness = new Harness(ToolCategory.ReadLocal, catalogDefault: false, policyTightens: true);
        harness.WithVerifiedMock("""{"schemaVersion":1,"rules":[{"field":"path","match":"Presence","response":"mocked body"}]}""");

        var outcome = await harness.Executor.ExecuteAsync(ToolName, Arguments, "teacher.gguf");

        AssertEx.Equal(HeadlessToolOutcomeKind.Mocked, outcome.Kind);
        AssertEx.Equal("mocked body", outcome.Result);
        AssertEx.Contains(outcome.Reason, "approval-gated");
    }

    [Test]
    public async Task HeadlessExecutor_ComposeCall_IsTheEnforcementPoint()
    {
        // The catalog offer itself says "no approval needed"; only the policy compose tightens it. If the executor read
        // the offer flag instead of calling the policy, this tool would execute for real.
        var harness = new Harness(ToolCategory.ReadLocal, catalogDefault: false, policyTightens: true);

        var outcome = await harness.Executor.ExecuteAsync(ToolName, Arguments, "teacher.gguf");

        _ = harness.Policy.Received(1).RequiresApproval(ToolName, ToolCategory.ReadLocal, false);
        AssertEx.NotEqual(HeadlessToolOutcomeKind.Executed, outcome.Kind);
    }

    [Test]
    public async Task HeadlessExecutor_NonReadLocalTool_NeverExecutesEvenWithoutApproval()
    {
        var harness = new Harness(ToolCategory.Network, catalogDefault: false, policyTightens: false);

        var outcome = await harness.Executor.ExecuteAsync(ToolName, Arguments, "teacher.gguf");

        AssertEx.Equal(HeadlessToolOutcomeKind.ValidationOnly, outcome.Kind);
        AssertEx.Contains(outcome.Reason, "not-read-local");
    }

    [Test]
    public async Task MockEngine_NoFallthrough()
    {
        // A tool the policy gates, with no usable mock, produces a visible validation-only outcome — never a real call.
        var harness = new Harness(ToolCategory.ReadLocal, catalogDefault: true, policyTightens: false);

        var outcome = await harness.Executor.ExecuteAsync(ToolName, Arguments, "teacher.gguf");

        AssertEx.Equal(HeadlessToolOutcomeKind.ValidationOnly, outcome.Kind);
        AssertEx.Contains(outcome.Reason, "no verified, enabled mock exists");
        AssertEx.Null(outcome.Result);
    }

    [Test]
    public async Task HeadlessExecutor_UnknownTool_FailsWithoutThrowing()
    {
        var harness = new Harness(ToolCategory.ReadLocal, catalogDefault: false, policyTightens: false);

        var outcome = await harness.Executor.ExecuteAsync("no_such_tool", Arguments, "teacher.gguf");

        AssertEx.Equal(HeadlessToolOutcomeKind.Failed, outcome.Kind);
    }

    private sealed class Harness
    {
        public Harness(ToolCategory category, bool catalogDefault, bool policyTightens)
        {
            var offer = new AllowedToolDto
            {
                Id = Guid.NewGuid(),
                Name = ToolName,
                Location = ToolLocation.ClientLocal,
                ParameterSchema = Schema,
                RequiresApproval = catalogDefault,
                Category = category
            };
            var offerProvider = Substitute.For<ILocalToolOfferProvider>();
            _ = offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns<IReadOnlyList<AllowedToolDto>>([offer]);

            Policy = Substitute.For<IToolApprovalPolicy>();
            _ = Policy.RequiresApproval(ToolName, category, catalogDefault).Returns(catalogDefault || policyTightens);

            Store = Substitute.For<ITrainingDatasetStore>();
            _ = Store.ListUsableMocksAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<ToolMockRecord>>([]);

            Executor = new HeadlessToolExecutor(offerProvider, Policy, new StubToolRegistry(), Store,
                new ToolMockEngine(), new ToolMockStaticVerifier(), NullLogger<HeadlessToolExecutor>.Instance);
        }

        public IHeadlessToolExecutor Executor { get; }

        public IToolApprovalPolicy Policy { get; }

        public ITrainingDatasetStore Store { get; }

        public void WithVerifiedMock(string body) =>
            _ = Store.ListUsableMocksAsync(ToolName, Arg.Any<CancellationToken>())
                     .Returns<IReadOnlyList<ToolMockRecord>>([
                         new ToolMockRecord(Guid.NewGuid(), ToolName, Encoding.UTF8.GetBytes(body), null,
                             ToolMockVerificationState.Verified, Enabled: true, Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0)
                     ]);
    }

    private sealed class StubToolRegistry : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools() =>
            [AIFunctionFactory.Create((string path) => $"read:{path}", ToolName)];

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors() =>
            [new LocalChatToolDescriptor(ToolName, "Reads a file.", Schema, RequiresApproval: false, ToolCategory.ReadLocal)];
    }
}
