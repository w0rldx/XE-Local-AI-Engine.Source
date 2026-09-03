namespace XE_Local_AI_Engine.AI.Agent.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     The escape hatch for the tool-relevance offer: an argument-free, approval-free listing of the tools this turn
///     held back, which also REVEALS them so the very next round of the same turn can call one.
///     <para>
///         <b>Binding is by object identity, not by key.</b> The send-time hop is the only layer that knows which tool
///         array it is filtering, and this function is built long before it runs — so the hop calls
///         <see cref="Bind" /> on the instance it finds in the INCOMING array, which is the same object
///         <c>FunctionInvokingChatClient</c> resolves calls against. Substituting a fresh instance into the hop's clone
///         instead would be provably dead: the function-invoking layer never consults the clone, so the substituted
///         object would never be invoked and the escape hatch would silently do nothing.
///     </para>
///     <para>
///         <b>An unbound invocation is defined, not exceptional.</b> On a round the hop passed through — at or below
///         the threshold, blank query, feature disabled, agent opted out — the slot is null, the function returns an
///         empty array, reveals nothing, and the turn continues.
///     </para>
///     <para>
///         <b>Stated exemption.</b> Because it is appended AFTER <c>InvocationToolResolver.ResolveAsync</c>, this
///         function is subject to neither the tighten-only node approval policy nor <c>AllowedToolNames</c>, and being
///         absent from the package's allowed-tool list it does not feed the turn's <c>approvalPossible</c> flag. That
///         is deliberate for an in-process listing of names the agent is already authorised for.
///     </para>
/// </summary>
internal sealed class ListToolsFunction : AIFunction
{
    /// <summary>The tool name, matched at the offer's core-set check and by the hop's gate.</summary>
    public const string ToolName = "list_tools";

    /// <summary>Longest description returned per tool; a listing is a menu, not a second copy of the schema.</summary>
    internal const int MaxDescriptionLength = 200;

    // Argument-free by design: an empty object schema keeps the compiled GBNF grammar's cost at zero, and a function
    // with no parameters is what rules out an ambient "current array" argument ever coming back.
    private static readonly JsonElement NoArgumentsSchema = MetadataToolFunction.ParseSchema("""
                                                                                             {
                                                                                               "type": "object",
                                                                                               "properties": {},
                                                                                               "additionalProperties": false
                                                                                             }
                                                                                             """);

    private static readonly JsonSerializerOptions ListingSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IList<AITool> _executableTools;

    private ArrayDecision? _decision;

    /// <param name="executableTools">
    ///     The turn's executable tool list, read only for each hidden tool's <see cref="AITool.Description" />. The
    ///     function never invokes anything in it.
    /// </param>
    public ListToolsFunction(IList<AITool> executableTools)
    {
        _executableTools = executableTools ?? throw new ArgumentNullException(nameof(executableTools));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Lists the tools that were held back from this turn to save context, with a one-line description each. "
        + "Call it when no offered tool fits what you were asked to do; the listed tools become callable immediately afterwards.";

    public override JsonElement JsonSchema => NoArgumentsSchema;

    /// <summary>
    ///     Binds the decision for the array this instance is about to be sent in. Called by the hop on the object it
    ///     located in the incoming array, immediately before the narrowed clone goes downstream.
    /// </summary>
    internal void Bind(ArrayDecision decision)
    {
        Volatile.Write(ref _decision, decision);
    }

    /// <summary>The currently bound decision, or <see langword="null" />. Test seam for the binding proof.</summary>
    internal ArrayDecision? BoundDecision => Volatile.Read(ref _decision);

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var decision = Volatile.Read(ref _decision);
        if (decision is null || decision.HiddenNames.Count == 0)
        {
            return ValueTask.FromResult<object?>("[]");
        }

        var listing = decision.HiddenNames
                              .Select(name => new HiddenTool(name, DescribeTool(name)))
                              .ToList();

        // Reveal AFTER the listing is materialised, on the decision this instance was bound to and on no other.
        decision.Reveal(decision.HiddenNames);

        return ValueTask.FromResult<object?>(JsonSerializer.Serialize(listing, ListingSerializerOptions));
    }

    private string DescribeTool(string name)
    {
        var description = _executableTools.FirstOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal))?.Description ?? string.Empty;

        // One line each: a newline in a description would otherwise turn a menu into a wall of text.
        description = description.ReplaceLineEndings(" ").Trim();

        return description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
    }

    private sealed record HiddenTool(string Name, string Description);
}
