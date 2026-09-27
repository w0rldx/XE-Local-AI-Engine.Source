namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Typed projection of a custom tool's opaque <c>ParametersJson</c> and <c>ConfigJson</c>: the persistence layer keeps them as opaque
///     strings, this layer owns their shape.
/// </summary>
/// <remarks>
///     Every type here is both a deserialization target for the operator-authored JSON and a serialization source, so the read and write
///     halves of a tool's config can never drift.
/// </remarks>
internal static class CustomToolJson
{
    /// <summary>The single options instance for custom-tool config (de)serialization.</summary>
    /// <remarks>
    ///     Case-insensitive, so the camelCase wire keys (<c>urlTemplate</c>, <c>isSecret</c>, …) bind to the PascalCase record members without
    ///     per-member attributes; camelCase on write, for a stable, operator-readable stored shape.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>A single declared input a Parameterized tool exposes to the model; a Fixed tool declares none.</summary>
/// <remarks>
///     <see cref="Type" /> is one of <c>string</c>, <c>number</c>, <c>integer</c> or <c>boolean</c> and is enforced at substitution time, so a
///     number param must arrive as a JSON number. The declaration itself is not sensitive — only the values the model supplies at run time are.
/// </remarks>
internal sealed record CustomToolParameter(string Name, string Type, string Description, bool Required);

/// <summary>An HTTP header a fetch tool sends. <see cref="IsSecret" /> marks a value that must be value-scrubbed from any log/model-facing string.</summary>
internal sealed record CustomToolHeader(string Name, string Value, bool IsSecret);

/// <summary>An extra environment variable a command tool injects. <see cref="IsSecret" /> marks a value that must be value-scrubbed from tool output.</summary>
internal sealed record CustomToolEnvironmentVariable(string Name, string Value, bool IsSecret);

/// <summary>Decrypted, typed <c>HttpFetch</c> configuration.</summary>
/// <remarks>
///     <see cref="UrlTemplate" /> may carry <c>{param}</c> placeholders in path and query positions only; when the host itself is
///     parameterized, <see cref="AllowedHosts" /> is mandatory and the SSRF guard enforces membership. Secret header values are carried in the
///     clear here, because building the request needs them, and are scrubbed from anything the model or a log sees.
/// </remarks>
internal sealed record HttpFetchConfig(
    string Method,
    string UrlTemplate,
    IReadOnlyList<CustomToolHeader> Headers,
    string? BodyTemplate,
    IReadOnlyList<string> AllowedHosts);

/// <summary>Decrypted, typed <c>Command</c> configuration.</summary>
/// <remarks>
///     <see cref="Executable" /> is a fixed absolute path, never a <c>{param}</c>, validated at execution time.
///     <see cref="ArgsTemplate" /> is one argv element per entry, and a <c>{param}</c> always substitutes into a single element, never a shell
///     string. Secret <see cref="Environment" /> values are injected through the child's environment, never argv, and scrubbed from its output.
/// </remarks>
internal sealed record CommandConfig(
    string Executable,
    IReadOnlyList<string> ArgsTemplate,
    string? WorkingDirectory,
    int TimeoutSeconds,
    IReadOnlyList<CustomToolEnvironmentVariable> Env);

/// <summary>Raised when a custom tool's persisted parameter or config JSON cannot be parsed into the typed contracts.</summary>
/// <remarks>
///     The executor catches it and returns a scrubbed, non-throwing tool-failure result, so a corrupt row is a failed tool call rather than an
///     aborted run.
/// </remarks>
public sealed class CustomToolConfigurationException : Exception
{
    public CustomToolConfigurationException()
    {
    }

    public CustomToolConfigurationException(string message)
        : base(message)
    {
    }

    public CustomToolConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Raised when a custom-tool invocation is blocked by a security guard: an SSRF denial, an undeclared placeholder, a type mismatch or a
///     rejected executable.
/// </summary>
/// <remarks>
///     The executor turns it into a non-throwing, secret-scrubbed tool-failure result the model can read, rather than a throw that would count
///     toward the run's abort threshold.
/// </remarks>
public sealed class CustomToolExecutionException : Exception
{
    public CustomToolExecutionException()
    {
    }

    public CustomToolExecutionException(string message)
        : base(message)
    {
    }

    public CustomToolExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
