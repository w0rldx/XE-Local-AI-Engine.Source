namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;

/// <summary>
///     Idempotent startup task that seeds ONE "Coder (read-only)" agent definition (slug
///     <see cref="AgentDefaults.CoderAgentSeedSlug" />) — a read-only project-access agent that can list, read, and
///     search a selected project inside the AgentHome sandbox. It carries the three coder tool names in
///     <c>AllowedToolNames</c> (so the agent-send intersection <c>offered ∩ AllowedToolNames</c> keeps them once the
///     offer merge is in place) with every tool approval set to <see langword="false" /> (the coder
///     tools are read-only and auto-run). It pins no model and disables the playbook.
///     <para>
///         <b>Idempotent + self-healing.</b> It seeds only when the slug is absent from
///         <see cref="IAgentDefinitionStore.ListSeededSlugsAsync" />, so re-runs never duplicate it. If an operator
///         deletes the seeded row, the next startup re-seeds it by slug. <b>Best-effort:</b> a node must start even if
///         seeding fails, so the expected failures are logged and swallowed and the next startup re-attempts. Mirrors
///         <see cref="DefaultAgentSeeder" />.
///     </para>
/// </summary>
public sealed class CoderAgentSeeder : IHostedService
{
    private const string Instructions =
        """
        You are a read-only coding agent operating inside a sandboxed copy of the user's selected project.

        You can ONLY observe the project — you have no ability to write, edit, delete, run commands, or change anything.
        Use your tools to answer questions about the code:
        - list_files: list files and folders in the workspace (optionally filtered by a glob).
        - read_file: read a UTF-8 text file, optionally a line range.
        - search_text: search the workspace for a text or regular-expression pattern.

        All paths are workspace-relative; never assume or request an absolute host path. Secret files and heavy
        generated directories (such as .git, .env, node_modules, bin, obj) are excluded and unavailable — do not try to
        read them. If no project workspace is available, tell the user to select a project folder first.

        Ground every claim in what you actually read or found. Quote the relevant file path and line when you reference
        code, and say so plainly when something is not present in the workspace rather than guessing.
        """;

    private readonly ILogger<CoderAgentSeeder> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public CoderAgentSeeder(IServiceScopeFactory scopeFactory, ILogger<CoderAgentSeeder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();

            var seededSlugs = await store.ListSeededSlugsAsync(cancellationToken).ConfigureAwait(false);
            if (seededSlugs.Contains(AgentDefaults.CoderAgentSeedSlug))
            {
                // The Coder agent already exists — nothing to seed (idempotent).
                return;
            }

            var seeded = await store.AddSeededAsync(BuildSeedInput(), AgentDefaults.CoderAgentSeedSlug, cancellationToken)
                                    .ConfigureAwait(false);

            _logger.LogInformation("Seeded the Coder (read-only) agent definition {AgentDefinitionId} (slug {SeedSlug}).",
                seeded.Id,
                AgentDefaults.CoderAgentSeedSlug);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down before startup finished; nothing to seed.
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DbUpdateException)
        {
            // Seeding is best-effort: a node must start even if the seed fails; the next startup re-attempts once the
            // underlying issue clears.
            _logger.LogWarning(ex, "Coder agent seeding failed at startup; the Coder agent definition may be missing until the next start.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The seed input for the Coder agent: the read-only coding prompt, a single-agent kind, no pinned
    ///     model/reasoning, the three coder tool names in the allowed set, every tool approval false, and
    ///     the playbook disabled.
    /// </summary>
    private static AgentDefinitionInput BuildSeedInput()
    {
        IReadOnlyList<string> allowedToolNames =
        [
            CoderToolDefinition.ListFilesToolName,
            CoderToolDefinition.ReadFileToolName,
            CoderToolDefinition.SearchTextToolName
        ];

        var toolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [CoderToolDefinition.ListFilesToolName] = false,
            [CoderToolDefinition.ReadFileToolName] = false,
            [CoderToolDefinition.SearchTextToolName] = false
        };

        return new AgentDefinitionInput(AgentDefaults.CoderAgentName,
            Description: "Read-only project-access agent: list, read, and search a selected project in the sandbox.",
            Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            allowedToolNames,
            toolApprovals,
            OrchestrationTopologyJson: null);
    }
}
