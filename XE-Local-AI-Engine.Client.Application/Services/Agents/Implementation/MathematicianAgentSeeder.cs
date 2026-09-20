namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Idempotent startup task that seeds ONE "Mathematician" agent definition: the persona that opts into the
///     sandboxed <c>run_python</c> compute tool through its <c>AllowedToolNames</c>.
/// </summary>
/// <remarks>
///     Seeding the persona is what makes the tool reachable at all — <c>run_python</c> is held out of the whole offer
///     and merged into the profile pool only for a profile naming it — and its approval stays <see langword="true" />,
///     because the seed opts INTO the tool, never out of the approval round-trip. Gated on <c>Compute:Enabled</c>,
///     since a disabled node refuses every call: the seeder skips, and never DELETES an already-seeded row. Idempotent
///     off <see cref="IAgentDefinitionStore.ListSeededSlugsAsync" />, best-effort like <see cref="CoderAgentSeeder" />.
/// </remarks>
public sealed class MathematicianAgentSeeder : IHostedService
{
    // The instructions ARE the feature: a model that merely HAS a calculator still asserts unverified arithmetic.
    // What changes the behaviour is being told a claim is unfinished until executed, and what to do on a disagreement.
    private const string Instructions =
        """
        You are a mathematician working with a sandboxed Python interpreter (numpy, scipy, sympy).

        Verify before you assert. Any time an answer depends on a computation — arithmetic beyond what you would
        confidently do on paper, algebra, calculus, a series, a numeric estimate, a combinatorial count, a probability,
        a unit conversion — you compute it with run_python FIRST and state the result you actually got. Do not present a
        remembered or estimated value as a computed one.

        How to use the tool well:
        - It runs one self-contained script per call. Nothing persists between calls: re-import and re-define what you
          need each time.
        - Print what you want to see. An expression's value is not returned on its own.
        - There is no network and no file you left behind. Everything the script needs must be in the script.
        - Prefer sympy for exact symbolic work (simplification, integrals, solving) and numpy/scipy for numeric work.
        - Cross-check a symbolic result numerically, and a numeric result against a second method, when the answer
          matters and the two are cheap.

        When the script disagrees with your reasoning, the script wins until you have found the bug. Read the traceback,
        fix the script, and run it again — a failed run is information, not a reason to fall back on an unverified
        answer. If several attempts still fail, say plainly what you could not verify rather than asserting it anyway.

        Show your reasoning in prose and quote the computed values you relied on. Keep the code you run visible and
        small enough that a reader can check it.
        """;

    private readonly bool _computeEnabled;
    private readonly ILogger<MathematicianAgentSeeder> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    // Reads the kill-switch through the validated options, as RunPythonToolHandler does, so ComputeOptions.Enabled
    // stays the single definition of "may this node execute code" rather than a second reading of raw configuration.
    public MathematicianAgentSeeder(IServiceScopeFactory scopeFactory,
        IOptions<ComputeOptions> computeOptions,
        ILogger<MathematicianAgentSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(computeOptions);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _computeEnabled = computeOptions.Value.Enabled;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_computeEnabled)
        {
            // One line per start, not per call: an operator who wonders where the agent went gets the reason and the
            // fix, and a node that never enables compute pays a single Information line at boot.
            _logger.LogInformation(
                "Skipped seeding the Mathematician agent definition because the compute tool is disabled on this node (Compute:Enabled=false); it is seeded on the next start after compute is enabled.");
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

            var seededSlugs = await store.ListSeededSlugsAsync(cancellationToken);
            if (seededSlugs.Contains(AgentDefaults.MathematicianAgentSeedSlug))
            {
                // The Mathematician already exists — nothing to seed (idempotent).
                return;
            }

            var seeded = await store.AddSeededAsync(BuildSeedInput(), AgentDefaults.MathematicianAgentSeedSlug, cancellationToken);

            _logger.LogInformation("Seeded the Mathematician agent definition {AgentDefinitionId} (slug {SeedSlug}).",
                seeded.Id,
                AgentDefaults.MathematicianAgentSeedSlug);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            // Seeding is best-effort: a node must start even if the seed fails; the next startup re-attempts once the
            // underlying issue clears.
            _logger.LogWarning(ex, "Mathematician agent seeding failed at startup; the Mathematician agent definition may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The seed input for the Mathematician: the verify-numerically prompt, a single-agent kind, no pinned
    ///     model/reasoning, <c>run_python</c> as the one allowed tool name, and its approval left ON.
    /// </summary>
    private static AgentDefinitionInput BuildSeedInput()
    {
        IReadOnlyList<string> allowedToolNames = [ComputeToolDefinition.ToolName];

        var toolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [ComputeToolDefinition.ToolName] = true
        };

        return new AgentDefinitionInput
        {
            Name = AgentDefaults.MathematicianAgentName,
            Description = "Verifies mathematical claims by running them in a sandboxed Python interpreter before asserting them.",
            Instructions = Instructions,
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = allowedToolNames,
            ToolApprovals = toolApprovals,
            OrchestrationTopologyJson = null
        };
    }
}
