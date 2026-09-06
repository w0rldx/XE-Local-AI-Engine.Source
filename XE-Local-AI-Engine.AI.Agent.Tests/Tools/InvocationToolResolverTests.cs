namespace XE_Local_AI_Engine.AI.Agent.Tests.Tools;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end resolution tests for the per-agent approval override. The policy is TIGHTEN-ONLY: a per-agent
///     offer can ADD an approval wrapper to a resolved executable but can never strip a handler- or MCP-enforced one, and
///     an offer with no policy metadata fails closed to requiring approval.
/// </summary>
public sealed class InvocationToolResolverTests
{
    [Test]
    public void Resolve_TightensClientLocalTool_WrapsResolvedExecutableInApproval()
    {
        // Handler registered NON-approval (a coder read tool). The per-agent offer tightens it to require approval, so
        // the resolver must wrap the resolved executable — the exact bug: a tightening override was silently discarded.
        var clientLocal = new FakeClientLocalToolRegistry(AIFunctionFactory.Create((string input) => input, "read_file"));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("read_file", requiresApproval: true)
        };

        var resolved = Resolve(offered, clientLocalToolRegistry: clientLocal);

        AssertEx.Equal(expected: 1, resolved.Count);
        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "a per-agent tightening of a ClientLocal tool must be honored");
    }

    [Test]
    public void Resolve_TightensBuiltInCatalogTool_WrapsResolvedExecutableInApproval()
    {
        var catalog = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("Calculate", requiresApproval: true)
        };

        var resolved = Resolve(offered, toolRegistry: catalog);

        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "a per-agent tightening of a built-in catalog tool must be honored");
    }

    [Test]
    public void Resolve_TightensSpawnSubagent_WrapsResolvedExecutableInApproval()
    {
        // spawn_subagent is a built-in resolved from the catalog; the audit found tightening it currently has no effect.
        var catalog = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "spawn_subagent"));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("spawn_subagent", requiresApproval: true)
        };

        var resolved = Resolve(offered, toolRegistry: catalog);

        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "tightening spawn_subagent must be honored via the same mechanism");
    }

    [Test]
    public void Resolve_LoosenAttempt_DoesNotUnwrapHandlerRequiredClientLocalTool()
    {
        // The registry returns a handler-required tool ALREADY approval-wrapped. A per-agent offer that says
        // requiresApproval=false must NOT unwrap it (tighten-only): the effective policy is handler OR per-agent.
        var alreadyWrapped = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string input) => input, "run_in_agent_home"));
        var clientLocal = new FakeClientLocalToolRegistry(alreadyWrapped);
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home", requiresApproval: false)
        };

        var resolved = Resolve(offered, clientLocalToolRegistry: clientLocal);

        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "a per-agent loosen must never strip a handler-enforced approval");
    }

    [Test]
    public void Resolve_LoosenAttempt_DoesNotUnwrapMcpApproval()
    {
        // MCP tools are always approval-wrapped. A per-agent loosen must not unwrap them.
        var wrapped = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string input) => input, "mcp__files__write_file"));
        var mcp = new FakeMcpToolRegistry(wrapped);
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("mcp__files__write_file", requiresApproval: false)
        };

        var resolved = Resolve(offered, mcpToolRegistry: mcp);

        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "a per-agent loosen must never strip an MCP-enforced approval");
    }

    [Test]
    public void Resolve_OfferWithNoPolicyMetadata_FailsClosedToApproval()
    {
        // A resolved tool whose offer is NOT a policy-carrying placeholder (a name collision / non-placeholder offer)
        // has no approval metadata — it must fail closed to requiring approval rather than auto-execute.
        var catalog = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "collide"));
        var offered = new[]
        {
            AIFunctionFactory.Create((string input) => input, "collide")
        };

        var resolved = Resolve(offered, toolRegistry: catalog);

        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "an offer with no policy metadata must fail closed to approval");
    }

    [Test]
    public void Resolve_HealthyNonApprovalTool_IsNotWrapped()
    {
        // The common case: a non-approval handler with a matching non-approval offer resolves to a plain executable.
        var catalog = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("Calculate", requiresApproval: false)
        };

        var resolved = Resolve(offered, toolRegistry: catalog);

        AssertEx.False(resolved[0] is ApprovalRequiredAIFunction, "a non-approval tool with a non-approval offer must not be wrapped");
    }

    [Test]
    public async Task ResolveAsync_ResolvesCustomToolName_ToTheApprovalWrappedExecutable()
    {
        // The catalog returns the executable ALREADY wrapped in ApprovalRequiredAIFunction (its authoritative floor). A
        // custom__ offered name that no built-in/ClientLocal/MCP registry satisfies must resolve from the catalog and stay
        // wrapped — the resolver's tighten-only pass is a no-op on an already-wrapped function.
        var wrapped = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string input) => input, "custom__weather"));
        var catalog = new FakeCustomToolCatalog(("custom__weather", wrapped));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("custom__weather", requiresApproval: true)
        };

        var resolved = await InvocationToolResolver.ResolveAsync(offered,
            new FakeToolRegistry(),
            new FakeClientLocalToolRegistry(),
            new FakeMcpToolRegistry(),
            catalog,
            NullLogger.Instance);

        AssertEx.Equal(expected: 1, resolved.Count);
        AssertEx.True(resolved[0] is ApprovalRequiredAIFunction, "a resolved custom tool must stay approval-wrapped");
        AssertEx.Equal("custom__weather", resolved[0].Name);
    }

    [Test]
    public async Task ResolveAsync_WhenCatalogReturnsNullForACustomName_DropsTheOffer()
    {
        // A custom offer the catalog cannot satisfy (kill-switch off, or the tool was deleted mid-turn) must be dropped,
        // never fabricated — the same posture as any unmatched offer.
        var catalog = new FakeCustomToolCatalog();
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("custom__weather", requiresApproval: true)
        };

        var resolved = await InvocationToolResolver.ResolveAsync(offered,
            new FakeToolRegistry(),
            new FakeClientLocalToolRegistry(),
            new FakeMcpToolRegistry(),
            catalog,
            NullLogger.Instance);

        AssertEx.Equal(expected: 0, resolved.Count, "an unresolved custom offer must be dropped, not fabricated");
    }

    [Test]
    public async Task ResolveAsync_WithSeveralDistinctCustomNames_QueriesTheCatalogOnce()
    {
        // O1: k distinct custom__ names cost ONE catalog round trip, not k. The catalog reads the whole library once
        // behind this seam, so batching here is what collapses a turn's store reads from k + 1 down to 2.
        var catalog = new FakeCustomToolCatalog(("custom__weather", Wrapped("custom__weather")),
            ("custom__stocks", Wrapped("custom__stocks")),
            ("custom__notes", Wrapped("custom__notes")));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("custom__weather", requiresApproval: true),
            InvocationToolBridge.CreateOfferPlaceholder("custom__stocks", requiresApproval: true),
            InvocationToolBridge.CreateOfferPlaceholder("custom__notes", requiresApproval: true)
        };

        var resolved = await InvocationToolResolver.ResolveAsync(offered,
            new FakeToolRegistry(),
            new FakeClientLocalToolRegistry(),
            new FakeMcpToolRegistry(),
            catalog,
            NullLogger.Instance);

        AssertEx.Equal(expected: 1, catalog.BatchCallCount, "three offered custom names must cost exactly one catalog call");
        AssertEx.Equal(expected: 3, catalog.LastRequestedNames.Count, "the single call must carry every distinct custom name");
        AssertEx.Contains(catalog.LastRequestedNames, "custom__weather");
        AssertEx.Contains(catalog.LastRequestedNames, "custom__stocks");
        AssertEx.Contains(catalog.LastRequestedNames, "custom__notes");
        AssertEx.Equal(expected: 3, resolved.Count, "every batched custom name must still resolve to an executable");
    }

    [Test]
    public async Task ResolveAsync_WithNoCustomNames_NeverCallsTheCatalog()
    {
        // The custom__ prefix filter is what keeps an ordinary offer off the custom-tool store entirely: with no custom
        // name in the offer the resolver must not open a batch call at all, so the common path pays no read.
        var catalog = new FakeCustomToolCatalog();
        var clientLocal = new FakeClientLocalToolRegistry(AIFunctionFactory.Create((string input) => input, "read_file"));
        var offered = new[]
        {
            InvocationToolBridge.CreateOfferPlaceholder("read_file", requiresApproval: false)
        };

        var resolved = await InvocationToolResolver.ResolveAsync(offered,
            new FakeToolRegistry(),
            clientLocal,
            new FakeMcpToolRegistry(),
            catalog,
            NullLogger.Instance);

        AssertEx.Equal(expected: 0, catalog.BatchCallCount, "an offer with no custom__ name must never reach the catalog");
        AssertEx.Equal(expected: 1, resolved.Count, "the non-custom offer must still resolve normally");
    }

    // The catalog's contract is that it hands back executables ALREADY wrapped in ApprovalRequiredAIFunction, so the
    // fakes above must hand back the same shape production does.
    private static AITool Wrapped(string name)
    {
        return new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string input) => input, name));
    }

    private static IList<AITool> Resolve(IReadOnlyList<AITool> offered,
        FakeToolRegistry? toolRegistry = null,
        FakeClientLocalToolRegistry? clientLocalToolRegistry = null,
        FakeMcpToolRegistry? mcpToolRegistry = null)
    {
        return InvocationToolResolver.Resolve(offered,
            toolRegistry ?? new FakeToolRegistry(),
            clientLocalToolRegistry ?? new FakeClientLocalToolRegistry(),
            mcpToolRegistry ?? new FakeMcpToolRegistry(),
            NullLogger.Instance);
    }

    private sealed class FakeToolRegistry(params AITool[] tools) : IAgentToolRegistry
    {
        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return [];
        }
    }

    private sealed class FakeClientLocalToolRegistry : IClientLocalToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeClientLocalToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(toolName, out tool);
        }
    }

    private sealed class FakeMcpToolRegistry : IMcpToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeMcpToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(name, out tool);
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return [];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            _tools.Clear();
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool.Executable;
            }
        }
    }

    private sealed class FakeCustomToolCatalog : ICustomToolCatalog
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeCustomToolCatalog(params (string Name, AITool Tool)[] tools)
        {
            foreach (var (name, tool) in tools)
            {
                _tools[name] = tool;
            }
        }

        /// <summary>How many batch round trips the resolver made — one per resolution, or zero when nothing is custom.</summary>
        public int BatchCallCount { get; private set; }

        /// <summary>The names the last batch call carried, so a test can prove they all travelled in one call.</summary>
        public IReadOnlyList<string> LastRequestedNames { get; private set; } = [];

        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(names);
            BatchCallCount++;
            LastRequestedNames = [.. names];

            var resolved = new Dictionary<string, AITool>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (_tools.TryGetValue(name, out var tool))
                {
                    resolved[name] = tool;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(resolved);
        }
    }
}
