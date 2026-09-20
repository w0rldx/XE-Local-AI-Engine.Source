namespace XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>
///     Application-layer orchestration over <see cref="XE_Local_AI_Engine.Client.Persistence.Stores.ICustomToolStore" />: validates
///     operator-authored custom tools and delegates persistence.
/// </summary>
/// <remarks>
///     The store owns id, version and timestamp stamping and the version-bump rule; this service never re-implements versioning. Validation is
///     the author-time trust boundary gating what reaches the executors: a MAF-safe <c>custom__</c> name, no collision with a built-in or MCP
///     name, a shell- and interpreter-free absolute command executable, a GBNF-safe parameter schema, every placeholder declared, the SSRF
///     allow-list for a parameterized fetch host, and — server-side, not just in the client checkbox — the danger acknowledgement.
/// </remarks>
public interface ICustomToolService
{
    /// <summary>Validates and persists a new custom tool, returning the stored view (secrets masked).</summary>
    Task<CustomToolView> CreateAsync(CustomToolDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies <paramref name="definition" /> to the tool with <paramref name="id" />. Returns the
    ///     updated view (secrets masked), or <c>null</c> when no tool has that id.
    /// </summary>
    Task<CustomToolView?> UpdateAsync(Guid id, CustomToolDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>Removes the tool with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the view for <paramref name="id" /> (secrets masked), or <c>null</c> when no tool has that id.</summary>
    Task<CustomToolView?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every custom tool as a masked view, ordered by Name (Ordinal).</summary>
    Task<IReadOnlyList<CustomToolView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Authoring-time validation of a candidate command executable: the same <see cref="HostExecutableGuard" /> checks the executor runs at
    ///     launch — absolute, non-interpreter, real regular file, no symlink.
    /// </summary>
    /// <remarks>Reports ok plus a reason, so the ProgramLaunch selector UI can validate a path the operator picks.</remarks>
    HostExecutableProbeResult ProbeExecutable(string? path);
}

/// <summary>Thrown when a custom-tool create or update fails validation.</summary>
/// <remarks>
///     The message is safe to surface to callers: it names the rule, the field, or a non-secret value such as a tool name or a fetch host, and
///     never echoes a secret header or env value.
/// </remarks>
public sealed class CustomToolValidationException : Exception
{
    public CustomToolValidationException(string message)
        : base(message)
    {
    }

    public CustomToolValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
