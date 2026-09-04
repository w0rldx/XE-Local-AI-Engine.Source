namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Text;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Turns an invoke body's inputs into the single seed user turn the run starts from, in caller order.
///     <para>
///         A JSON input is ALWAYS fenced with <see cref="UntrustedContentFraming.WrapDocument(string?, IReadOnlyList{KeyValuePair{string, string?}})" />,
///         never concatenated raw: it is attacker-controlled data arriving over an API, and its label is
///         attacker-controlled too, so both go inside one fence whose per-call nonce the author cannot predict.
///     </para>
///     <para>
///         It does NOT truncate. The caller enforces the seed ceiling and answers 422, because silently trimming an
///         external payload changes the meaning of the request without telling anyone.
///     </para>
/// </summary>
public static class IntegrationSeedComposer
{
    private const string BlockSeparator = "\n\n";

    public static string Compose(IReadOnlyList<IntegrationInputDto> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var blocks = new List<string>(inputs.Count);
        foreach (var input in inputs)
        {
            if (input.Kind == IntegrationInputKinds.Json)
            {
                blocks.Add(UntrustedContentFraming.WrapDocument(input.Json,
                [
                    new KeyValuePair<string, string?>("label", input.Label ?? "json"),
                    new KeyValuePair<string, string?>("contentType", "application/json")
                ]));
                continue;
            }

            blocks.Add(input.Text?.Trim() ?? string.Empty);
        }

        return string.Join(BlockSeparator, blocks);
    }

    /// <summary>The composed seed's size against the configured ceiling, measured the way the ceiling is expressed.</summary>
    public static int Utf8ByteCount(string seed) =>
        Encoding.UTF8.GetByteCount(seed);
}
