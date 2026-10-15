namespace XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>The outcome of one custom-tool run: the scrubbed, model-readable text and whether the action itself succeeded.</summary>
/// <remarks>
///     <see cref="Succeeded" /> is false for a blocked, misconfigured, failed-to-send or timed-out call, a non-2xx HTTP response and a
///     non-zero exit code, so a graph action node can fault on it. The chat path reads only <see cref="Text" />.
/// </remarks>
internal sealed class CustomToolExecution
{
    public required string Text { get; init; }

    public required bool Succeeded { get; init; }

    public static CustomToolExecution Success(string text)
    {
        return new CustomToolExecution
        {
            Text = text,
            Succeeded = true
        };
    }

    public static CustomToolExecution Failure(string text)
    {
        return new CustomToolExecution
        {
            Text = text,
            Succeeded = false
        };
    }
}
